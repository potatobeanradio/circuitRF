using CircuitRF.Core.Devices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Stability;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using Em3dPort = CircuitRF.Engine.Em3d.Em3dPort;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using System.Numerics;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf explain &lt;path&gt; [--expr … | --analysis [name] | --ref …]</c> — what did circuitRF
/// decide? (brief-automation-4-check-and-explain.md §3.)
///
/// <para><b>This verb answers a question that is not a failure.</b> <c>check</c> asks whether a
/// design is sound; <c>explain</c> asks what it RESOLVED TO — which technology a layout landed on and
/// through which workspace, which chain <c>SelectTop</c> would dispatch, what an expression evaluates
/// to, where a relative reference points. That question is asked constantly while authoring, and
/// before this verb the only way to answer it was to run something and read stderr.</para>
///
/// <para><b>The walk is reported, not just the answer</b> (R-aut4-7). A <c>.cem</c>'s layout walk and
/// its technology walk start from different files and can legitimately land on different workspaces
/// (<c>cli.md</c> §8.1) — that is deliberate, and it is exactly the thing a caller cannot otherwise
/// see. So every resolution comes back as a step: what was being resolved, from where, to what, by
/// which rule.</para>
///
/// <para><b>Nothing is guessed and nothing falls back silently</b> (R-aut4-8). Where resolution
/// fails, the failure IS the answer, reported as a diagnostic naming what was looked for and where it
/// was looked — because a caller reaches for this verb precisely when something did not resolve.</para>
/// </summary>
internal static class Explain
{
    /// <summary>
    /// The three analysis kinds that go through chain selection, with the arguments the run verbs
    /// themselves pass. Shared with <see cref="Check"/> so neither grows its own copy — and named
    /// here rather than in <c>Program.cs</c> because <c>Program.cs</c> is top-level statements and
    /// nothing outside it can reach a local.
    ///
    /// <para><c>sparam</c> and <c>dc</c> are deliberately absent: neither uses <c>SelectTop</c> —
    /// <c>sparam</c> takes the first typed <c>SParameterAnalysis</c> and <c>dc</c> needs no
    /// directive at all — so listing them here would claim a promotion rule they do not have. They
    /// still appear in the report, as declared analyses no chain selection applies to.</para>
    /// </summary>
    public static readonly (Func<Analysis, bool> IsBase, string Label, string Hint, string Verb)[] AnalysisKinds =
    [
        (a => a is HarmonicBalanceAnalysis,  "HB",               "analysis <name> type=hb ...",                "hb"),
        (a => a is LoadpullAnalysis,         "loadpull",         "analysis <name> type=loadpull ...",          "lp"),
        (a => a is LoadpullPursuitAnalysis,  "loadpull-pursuit", "analysis <name> type=loadpull_pursuit ...",  "lpp"),
    ];

    public static int Run(string[] args)
    {
        string? path = null, expr = null, reference = null, analysisName = null, setupName = null, objectName = null;
        bool wantAnalyses = false, wantCells = false, wantLayers = false, wantExtents = false, all = false;
        bool wantFootprints = false, wantLook = false, wantTunables = false;
        ViewType? askedView = null;
        var sets = new List<(string Name, string Expr)>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // RND-3's three. They are OPTIONS and not verbs for R-rnd3-1's reason, and they join
                // the one-question rule below rather than getting an exception from it (R-rnd3-2).
                case "--cells":   wantCells   = true; continue;
                case "--layers":  wantLayers  = true; continue;
                case "--extents": wantExtents = true; continue;
                // brief-footprint-4 R-fp4-4b. An OPTION and not a verb, on R-rnd3-1's terms, and it
                // joins the one-question rule below rather than getting an exception from it.
                case "--footprints": wantFootprints = true; continue;
                // TO-1 R-to1-7 — every tunable value of a schematic or netlist, and the setup keys that name nothing.
                case "--tunables": wantTunables = true; continue;
                case "--all":     all         = true; continue;
                case "--view" when i + 1 < args.Length:
                {
                    // Only ever a disambiguator for a cell folder, and spelled exactly as `render`
                    // spells it — the two verbs are used together and a second spelling of one idea is
                    // a trap.
                    string v = args[++i];
                    askedView = v.ToLowerInvariant() switch
                    {
                        "schematic" => ViewType.Schematic,
                        "symbol"    => ViewType.Symbol,
                        "layout"    => ViewType.Layout,
                        _           => null,
                    };
                    if (askedView is null)
                    {
                        JsonRun.Report(CliDiagnostics.ExplainViewUnknown(v));
                        return Usage();
                    }
                    continue;
                }
                case "--expr" when i + 1 < args.Length:
                    expr = args[++i];
                    continue;
                case "--ref" when i + 1 < args.Length:
                    reference = args[++i];
                    continue;
                // Designer feedback round 11 — which of a .c3d's embedded setups to explain, spelled as `em` and
                // `render` spell it. A view with several setups names this flag when it cannot choose one itself.
                case "--setup" when i + 1 < args.Length:
                    setupName = args[++i];
                    continue;
                // brief-em3d-105 R-em3d105-7c — one object of a .c3d: how it looks, and which statement decided each field.
                case "--object" when i + 1 < args.Length:
                    objectName = args[++i];
                    continue;
                // brief-em3d-110 R-em3d110-1g — a .c3d's Look as `render --look realistic` resolves it, and its appearance table.
                case "--look": wantLook = true; continue;
                case "--set" when i + 1 < args.Length:
                {
                    // The same override the run verbs take, applied the same way (cli.md §5): it
                    // REPLACES the variable in the netlist's own scope, so everything derived from
                    // it re-derives. That is the whole point of asking `explain` about it — "what
                    // does this expression become if I set Pavl_dbm to 0" is a question about the
                    // scope, and an override pushed anywhere else would answer a different one.
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) { JsonRun.Report(CliDiagnostics.SetMalformed("explain", kv)); return Usage(); }
                    sets.Add((kv[..eq].Trim(), kv[(eq + 1)..].Trim()));
                    continue;
                }
                case "--analysis":
                    wantAnalyses = true;
                    // The name is optional: `--analysis` alone lists every chain, `--analysis SW1`
                    // asks what would happen if SW1 were named to a run verb. A following token that
                    // starts with '-' is the next option, not a name.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) analysisName = args[++i];
                    continue;
                default:
                    if (args[i].StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.ExplainUnknownOption(args[i])); return Usage(); }
                    if (path is not null)
                    { JsonRun.Report(CliDiagnostics.ExplainMultiplePaths()); return Usage(); }
                    path = args[i];
                    continue;
            }
        }

        if (path is null) { JsonRun.Report(CliDiagnostics.ExplainPathRequired()); return Usage(); }
        JsonRun.InputPath = path;

        int asked = (expr is null ? 0 : 1) + (reference is null ? 0 : 1) + (wantAnalyses ? 1 : 0)
                  + (wantCells ? 1 : 0) + (wantLayers ? 1 : 0) + (wantExtents ? 1 : 0)
                  + (wantFootprints ? 1 : 0) + (objectName is null ? 0 : 1) + (wantLook ? 1 : 0)
                  + (wantTunables ? 1 : 0);
        if (asked > 1) { JsonRun.Report(CliDiagnostics.ExplainOneQuestion()); return Usage(); }
        if (all && !wantCells) { JsonRun.Report(CliDiagnostics.ExplainAllNeedsCells()); return Usage(); }

        if (!File.Exists(path) && !Directory.Exists(path))
            return JsonRun.Fail(CliDiagnostics.ExplainPathNotFound(path));

        var kind  = DocumentKinds.Classify(path);
        if (setupName is not null && kind != DocumentKind.ThreeD)
            return JsonRun.Fail(kind == DocumentKind.EmSetup ? CliDiagnostics.EmSetupOnCem(path)
                                                             : CliDiagnostics.SetupNotAThreeDView("explain", path));
        var walks = new List<ResolutionStepJson>();
        int exit  = 0;

        IReadOnlyList<ExplainAnalysisJson>? analyses = null;
        ExplainExpressionJson?              value    = null;
        ExplainReferenceJson?               refRes   = null;
        IReadOnlyList<ExplainCellJson>?     cells    = null;
        ExplainLayersJson?                  layers   = null;
        ExplainExtentsJson?                 extents  = null;
        IReadOnlyList<ExplainFootprintJson>? footprints = null;
        ExplainEm3dJson?                    em3d     = null;
        IReadOnlyList<CircuitRF.Design.ThreeD.SetupSolveStatus>? solved = null;
        ExplainTunablesJson?                tunables = null;
        ExplainStatisticsJson?              statistics = null;
        ExplainDistributionsJson?           distributions = null;

        // The document's OWN resolution always runs, whatever was asked: "which workspace, which
        // technology" is context for every other answer, and a report that omitted it would leave a
        // caller unable to tell which process an expression or a rule was read against.
        switch (kind)
        {
            case DocumentKind.Layout:   ExplainLayout(path, walks); break;
            case DocumentKind.EmSetup:  exit |= ExplainEmSetup(path, walks, out em3d); break;
            case DocumentKind.ThreeD:
                exit |= ExplainThreeD(path, walks, out em3d, sets, setupName, objectName);
                solved = Solved.Of(Path.GetFullPath(path));          // brief-em3d-98 R-em3d98-8
                break;
            case DocumentKind.Technology:      exit |= ExplainTechnology(path, walks); break;
            case DocumentKind.MaterialLibrary: ExplainMaterialLibrary(path, walks); break;
            // A circuit reports its workspace walk here; its analyses are --analysis's. (Brief 53 had put the technology case
            // between these and the walk, so every .cnl, .csch and cell was read as a .ctech — "';' is an invalid start".)
            case DocumentKind.Netlist:
            case DocumentKind.Schematic:
            case DocumentKind.Cell:
            case DocumentKind.Workspace:
            case DocumentKind.Symbol:
            case DocumentKind.AssemblyRules:
            case DocumentKind.Rail:
            case DocumentKind.Smith:
            case DocumentKind.Folder:
                // Nothing but the workspace walk to report: none of these resolves a second
                // reference of its own. Reported anyway, because "which workspace" is the context
                // every other answer is read against.
                Workspace(path, walks);
                if (kind is DocumentKind.Netlist or DocumentKind.Schematic) CircuitTechnology(path, kind, walks);
                if (kind is DocumentKind.Netlist or DocumentKind.Schematic) PhysicalLines(path, kind, walks);
                if (kind is DocumentKind.Schematic) ArtworkSource(path, walks);
                if (kind is DocumentKind.Schematic) ImageSourceBlock(path, walks);
                break;
            case DocumentKind.Interchange:
                walks.Add(new ResolutionStepJson(
                    "format", path, DocumentKinds.InterchangeFormat(path),
                    "content and extension, through `convert`'s own classifier"));
                break;
            case DocumentKind.Touchstone:
                exit |= ExplainTouchstone(path, walks);
                break;
            case DocumentKind.Picture:
                exit |= ExplainPicture(path, walks);
                break;
            default:
                return JsonRun.Fail(CliDiagnostics.ExplainUnknownKind(path));
        }

        if (objectName is not null && kind != DocumentKind.ThreeD)
            exit |= JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable("--object", DocumentKinds.Name(kind), "one object of a 3D view"));
        if (wantLook)
            exit |= kind == DocumentKind.ThreeD ? ExplainLook.Walk(path, setupName, walks)
                  : JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable("--look", DocumentKinds.Name(kind), "a 3D view's Look and appearances"));

        if (reference is not null)
        {
            var (resolved, refExit) = ExplainReference(path, reference);
            refRes = resolved;
            exit  |= refExit;
        }

        if (wantCells)
        {
            var (rows, cellExit) = ExplainQueries.Cells(path, kind, all);
            cells = rows.Count > 0 || cellExit == 0 ? rows : null;
            exit |= cellExit;
        }

        if (wantLayers)
        {
            var (report, layerExit) = ExplainQueries.Layers(path, kind, askedView, walks);
            layers = report;
            exit  |= layerExit;
        }

        if (wantExtents)
        {
            var (report, extentExit) = ExplainQueries.Extents(path, kind, askedView);
            extents = report;
            exit   |= extentExit;
        }

        if (wantFootprints)
        {
            var (rows, fpExit) = ExplainQueries.Footprints(path, kind, walks);
            footprints = rows.Count > 0 || fpExit == 0 ? rows : null;
            exit |= fpExit;
        }

        if (wantTunables)
        {
            var (report, tunableExit) = ExplainTunables.Collect(path, kind);
            tunables = report;
            exit    |= tunableExit;
        }

        // brief-em3d-51 R-em3d51-5c — a 3D view's --expr evaluates in the DOCUMENT's resolved scope (its cell's parameters
        // and its VARs), with --set applied first, as a circuit's does in its global scope.
        if (expr is not null && kind == DocumentKind.ThreeD)
        {
            exit |= ExplainThreeDExpression(path, expr, sets, out value);
            expr = null;
        }

        // brief-em3d-73 R-em3d73-6b — a 3D view's analyses are its thermal setups, resolved against its own scope.
        if (wantAnalyses && kind == DocumentKind.ThreeD)
        {
            exit |= ExplainThermal.Walk(path, analysisName, sets, walks);
            wantAnalyses = false;
        }

        if (expr is not null || wantAnalyses)
        {
            var circuit = ReadCircuit(path, kind);
            if (circuit is null)
            {
                exit |= JsonRun.Fail(CliDiagnostics.ExplainNotApplicable(
                    expr is not null ? "--expr" : "--analysis", DocumentKinds.Name(kind)));
            }
            else
            {
                var (lib, tb) = circuit.Value;

                foreach (var (name, expression) in sets)
                {
                    tb.GlobalVariables.RemoveAll(v => v.Name == name);
                    tb.GlobalVariables.Add(new Variable(name, expression));
                    Console.Error.WriteLine($"[circuitRF] set {name} = {expression}");
                }

                if (wantAnalyses)
                {
                    var (rows, analysisExit) = ExplainAnalyses(lib, tb, analysisName);
                    analyses = rows;
                    exit    |= analysisExit;
                    statistics    = ExplainStatistics.Collect(lib, tb, path);
                    distributions = ExplainStatistics.Distributions(lib, tb);
                }
                if (expr is not null) exit |= ExplainExpression(lib, tb, expr, out value);
            }
        }

        JsonRun.Explain = new ExplainReportJson(
            path, DocumentKinds.Name(kind), walks, analyses, value, refRes, cells, layers, extents,
            footprints, em3d, solved is null ? null : Solved.ForExplain(solved), tunables, statistics, distributions);

        Print(path, kind, walks, analyses, value, refRes, cells, layers, extents, footprints);
        if (em3d is not null) ExplainEm3d.Print(em3d);
        if (solved is not null) Solved.Print(solved);
        if (tunables is not null) ExplainTunables.Print(tunables);
        if (statistics is not null) ExplainStatistics.Print(statistics);
        if (distributions is not null) ExplainStatistics.PrintDistributions(distributions);
        return exit;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf explain <path> [--expr \"<expression>\"] [--set var=expr]");
        Console.Error.WriteLine("                            [--analysis [<name>]] [--ref <relative-ref>]");
        Console.Error.WriteLine("                            [--cells [--all]] [--layers] [--extents] [--view <name>]");
        Console.Error.WriteLine("                            [--footprints] [--setup <name>] [--object <name>] [--look]");
        Console.Error.WriteLine("                            [--tunables]");
        return 1;
    }

    // ── a Touchstone file ────────────────────────────────────────────────────

    /// <summary>
    /// <c>explain part.s2p</c> — what IS this part?
    ///
    /// <para><b>Why this belongs to <c>explain</c> and not to <c>check</c>.</b> The two verbs split
    /// on soundness versus decision, and "is this file passive, sorted and causal" is soundness
    /// while "what is its self-resonance and how many milliohms is it there" is not a defect at
    /// all — it is what the file SAYS. A designer holding a vendor's part file wants the second
    /// question answered far more often than the first, and before this there was no way to ask it
    /// but to build a schematic around the file and plot it.</para>
    ///
    /// <para><b>Every applicable fixture is reported, side by side, rather than one being picked.</b>
    /// Nothing in Touchstone records how the part was measured, and the readings differ by orders of
    /// magnitude — so choosing one silently would be exactly the failure this feature exists to
    /// prevent. Seeing all of them is also the fastest way to identify an unlabelled file: only the
    /// physical reading gives a sensible self-resonance and a milliohm-scale ESR, and the others are
    /// obviously nonsense next to it.</para>
    /// </summary>
    private static int ExplainTouchstone(string path, List<ResolutionStepJson> walks)
    {
        SNP snp;
        try { snp = TouchstoneIO.ReadFile(path, readComments: false); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.CheckUnreadable(path, ex.Message)); }

        var h = TouchstoneHealth.Analyze(snp);

        walks.Add(new ResolutionStepJson("ports", path, h.Ports.ToString(),
            "the `.sNp` extension, cross-checked against the data block"));
        walks.Add(new ResolutionStepJson("sweep", path,
            h.FrequencyCount == 0 ? null : $"{h.FrequencyCount} points, {Check.Hz(h.FirstFrequencyHz)} to {Check.Hz(h.LastFrequencyHz)}"
            + (h.GridUniform ? ", uniform" : ", non-uniform"),
            "the file's own frequency column, in the unit its option line declares"));
        walks.Add(new ResolutionStepJson("reference impedance", path, Check.Ohms(h.Z0),
            "the `R` field of the option line"));

        if (h.FrequencyCount == 0 || h.Ports == 0) return 0;

        var z0   = Enumerable.Repeat(snp.Z0, snp.Ports).ToArray();
        var mats = snp.Matrices;

        var fixtures = h.Ports >= 2
            ? new[] { PassiveExtraction.ShuntThrough, PassiveExtraction.SeriesThrough }
            : [PassiveExtraction.OnePort];

        foreach (var mode in fixtures)
        {
            Complex[] z;
            try { z = PassiveMetrics.Impedance(mats, z0, mode, 1, Math.Min(2, h.Ports)); }
            catch (ArgumentException) { continue; }

            string how = mode switch
            {
                PassiveExtraction.ShuntThrough  => "the SHUNT-through reading, Z = (Z0/2)·S21/(1−S21)",
                PassiveExtraction.SeriesThrough => "the SERIES-through reading, Z = 2·Z0·(1−S21)/S21",
                _                               => "the 1-port reflection reading, Z = Z0·(1+S11)/(1−S11)",
            };
            string label = mode switch
            {
                PassiveExtraction.ShuntThrough  => "shunt-through",
                PassiveExtraction.SeriesThrough => "series-through",
                _                               => "1-port",
            };

            // The self-resonance and the impedance floor beside it: the two numbers a decoupling
            // capacitor is chosen on, and the pair that says at a glance whether this fixture is
            // the physical reading of the file.
            var srf = PassiveMetrics.SelfResonance(snp.Frequencies, z);
            walks.Add(new ResolutionStepJson(
                $"SRF ({label})", path,
                srf is { } f ? Check.Hz(f) : null,
                srf is null
                    ? how + " — no capacitive-to-inductive reactance crossing inside this sweep"
                    : how + ", lowest capacitive-to-inductive reactance crossing"));

            int iMin = -1;
            double best = double.PositiveInfinity;
            for (int i = 0; i < z.Length; i++)
            {
                double m = z[i].Magnitude;
                if (double.IsFinite(m) && m < best) { best = m; iMin = i; }
            }
            if (iMin >= 0)
                walks.Add(new ResolutionStepJson(
                    $"|Z| min ({label})", path,
                    $"|Z| = {Ohm(best)} at {Check.Hz(snp.Frequencies[iMin])}, ESR {Ohm(z[iMin].Real)}",
                    how + " — the sweep's minimum |Z| and the resistance there"));
        }

        return 0;
    }

    /// <summary>An impedance with an SI prefix, because a decoupling capacitor's floor is
    /// milliohms and a series capacitor's is megohms, in the same report.</summary>
    private static string Ohm(double r) =>
        !double.IsFinite(r)       ? "?"
        : Math.Abs(r) >= 1e6      ? $"{r / 1e6:0.###} MΩ"
        : Math.Abs(r) >= 1e3      ? $"{r / 1e3:0.###} kΩ"
        : Math.Abs(r) >= 1        ? $"{r:0.###} Ω"
        : Math.Abs(r) >= 1e-3     ? $"{r * 1e3:0.###} mΩ"
        :                           $"{r * 1e6:0.###} µΩ";

    // ── the walks ────────────────────────────────────────────────────────────

    /// <summary>The ancestor-workspace walk every document-relative reference resolves through.</summary>
    private static string? Workspace(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        string? cws = DocumentKinds.AncestorCws(full);
        walks.Add(new ResolutionStepJson(
            "workspace", full, cws,
            cws is null
                ? "nearest ancestor .cws — none found, so references resolve against the document's own directory"
                : "nearest ancestor .cws"));
        return cws;
    }

    /// <summary>
    /// brief-artsch-6 R-as6-1 — which technology a schematic or a netlist resolved, and through which walk: its
    /// own reference (a <c>.csch</c>'s <c>TechRef</c>, a <c>.cnl</c>'s <c>technology</c> statement) first, the
    /// workspace default only without one. Resolved by <see cref="SchematicTechnology"/>, the resolver the
    /// extraction and the netlist binding use, so this reports what a run would use.
    /// </summary>
    private static void CircuitTechnology(string path, DocumentKind kind, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        string? techRef;
        try
        {
            techRef = kind == DocumentKind.Schematic
                ? SchematicPersistence.LoadFromFile(full).model.TechRef
                : CircuitRF.Core.Netlist.CnlReader.ReadFile(full).TestBench.Technology;
        }
        catch (Exception) { return; }   // the walk is context; a document that does not read is reported elsewhere

        var res = SchematicTechnology.Resolve(dir, techRef);
        walks.Add(new ResolutionStepJson("technology", techRef ?? DocumentKinds.AncestorCws(full), res.Path, res.Walk));
        if (res.Error is { } error) JsonRun.Report(CliDiagnostics.CheckResolverNote(path, error));
    }

    /// <summary>
    /// brief-img-2 R-im2-6 — a picture: its format and size, the kind of drawing it reads as, and every measurement the
    /// reading weighed, in the order <see cref="CircuitRF.Design.Imaging.ImageKind.Classify"/> took them. Nothing past IM-2 is read.
    /// </summary>
    private static int ExplainPicture(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        if (PictureKinds.Read(full, out string? refusal) is not { } p)
            return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, refusal ?? "not a picture"));

        var r = p.Source.Raster;
        walks.Add(new ResolutionStepJson("picture format", full, p.Source.Format,
            "the file's signature, whatever its extension says"));
        walks.Add(new ResolutionStepJson("picture size", full,
            $"{r.Width} × {r.Height} px" + (r.ReductionFactor < 1 ? $", reduced by {Inv(r.ReductionFactor, "0.###")} to 50 MP" : ""),
            r.StatedResolution is { } dpi
                ? $"the decoded picture, turned upright; the file states {Inv(dpi.XDpi, "0.#")} × {Inv(dpi.YDpi, "0.#")} dpi ({dpi.Source}), never used as a scale"
                : "the decoded picture, turned upright"));
        foreach (var e in p.Kind.Evidence)
            walks.Add(new ResolutionStepJson("picture " + e.Name, full,
                Inv(e.Value, "0.####"), "the kind reading; it leans " + e.Reads));
        walks.Add(new ResolutionStepJson("picture kind", full,
            $"{p.Kind.Name}, confidence {Inv(p.Kind.Confidence, "0.00")}"
                + (p.Kind.Reason is { } why ? $": {why}" : ""),
            "the measurements above — a suggestion, which Create from Image and --image-kind can override"));
        return 0;

        static string Inv(double v, string format) => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>brief-img-2 R-im2-5 — a schematic created from a picture: its <c>ImageSource</c> block in plain lines.</summary>
    private static void ImageSourceBlock(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        CircuitRF.Design.Imaging.ImageProvenance? source;
        try { source = SchematicPersistence.LoadFromFile(full).model.ImageSource; }
        catch (Exception) { return; }   // a document that does not read is reported elsewhere
        if (source is null) return;

        string kept = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, source.Picture));
        walks.Add(new ResolutionStepJson("image source", source.Picture, File.Exists(kept) ? kept : null,
            File.Exists(kept) ? $"the kept copy of '{source.OriginalName}', relative to the schematic"
                              : $"the kept copy of '{source.OriginalName}' is not there any more"));
        walks.Add(new ResolutionStepJson("image read as", full,
            source.Kind + (source.KindForced == true ? " (chosen)" : " (read)") + $", {source.PixelWidth} × {source.PixelHeight} px, sha256 {source.Sha256}",
            "a re-run may replace this schematic; one without this block is never replaced"));
    }

    /// <summary>
    /// brief-artsch-7 R-as7-8 — a schematic created from artwork: its <c>ArtworkSource</c> block in plain lines, the
    /// source <c>.clay</c> resolved against the schematic's own directory (what <c>--ref</c> resolves too).
    /// </summary>
    private static void ArtworkSource(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        ArtworkProvenance? source;
        try { source = SchematicPersistence.LoadFromFile(full).model.ArtworkSource; }
        catch (Exception) { return; }   // a document that does not read is reported elsewhere
        if (source is null) return;

        string clay = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, source.Layout));
        walks.Add(new ResolutionStepJson("artwork source", source.Layout, File.Exists(clay) ? clay : null,
            File.Exists(clay) ? "the layout this schematic was created from, relative to the schematic"
                              : "the layout this schematic was created from is not there any more"));
        walks.Add(new ResolutionStepJson("artwork scope", full,
            source.Scope + (source.Rings is { Count: > 0 } rings ? $" ({rings.Count} ring(s), DBU)" : ""), "the part of the layout read"));
        if (source.Options.Count > 0)
            walks.Add(new ResolutionStepJson("artwork options", full,
                string.Join(", ", source.Options.Select(kv => $"{kv.Key}={kv.Value}")), "what the recognition was told"));
        if (source.PartsCsvSha256 is { } sha)
            walks.Add(new ResolutionStepJson("artwork parts table", full, $"sha256 {sha}", "the edited parts table it read"));
        walks.Add(new ResolutionStepJson("artwork written", full, $"circuitRF {source.Version}, {source.CreatedUtc}",
            "a re-run may replace this schematic; one without this block is never replaced"));
    }

    /// <summary>
    /// brief-em3d-53 R-em3d53-7 — a technology's libraries: where each reference lands and what it
    /// contributed. A technology whose libraries refuse to load says so, with the refusal's own sentence,
    /// and exits 1.
    /// </summary>
    private static int ExplainTechnology(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        Workspace(path, walks);

        Technology own;
        try { own = TechPersistence.LoadOwnFromFile(full); }
        catch (Exception ex) { JsonRun.Report(CliDiagnostics.ExplainUnreadable(path, ex.Message)); return 1; }
        if (own.MaterialLibraries is not { Count: > 0 } refs)
        {
            walks.Add(new ResolutionStepJson("material libraries", full, null,
                $"none named; its {own.Materials.Count} material(s) are its own"));
            return 0;
        }

        Technology? tech = null;
        string? refusal = null;
        try { tech = TechPersistence.LoadFromFile(full); }
        catch (MaterialLibraryException ex) { refusal = ex.Message; }

        foreach (string reference in refs)
        {
            string lib = MaterialLibraries.ResolvePath(full, reference);
            var names = tech?.LibraryMaterials.Where(m => string.Equals(m.SourcePath, lib, StringComparison.OrdinalIgnoreCase))
                             .Select(m => m.Material.Name).ToList();
            walks.Add(new ResolutionStepJson($"library {reference}", full, lib,
                "relative to the .ctech's own directory" +
                (names is null ? "" : names.Count == 0 ? "; contributes no material"
                    : $"; contributes {string.Join(", ", names.Select(n => $"'{n}'"))}")));
        }
        if (refusal is not null)
        {
            walks.Add(new ResolutionStepJson("material libraries", full, null, "REFUSED: " + refusal));
            return 1;
        }
        walks.Add(new ResolutionStepJson("materials", full, null,
            $"{tech!.Materials.Count} own and {tech.ResolvedMaterials.Count - tech.Materials.Count} from libraries; " +
            "a name two sources define must carry equal values"));
        return 0;
    }

    /// <summary>brief-em3d-53 R-em3d53-7 — a library: the technologies in its workspace that name it, since an
    /// edit to it changes every one of them.</summary>
    private static void ExplainMaterialLibrary(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        string? cws = Workspace(path, walks);
        string root = cws is null ? Path.GetDirectoryName(full)! : Path.GetDirectoryName(cws)!;
        var users = MaterialLibraries.TechnologiesNaming(full, root);
        if (users.Count == 0)
            walks.Add(new ResolutionStepJson("named by", full, null,
                "no technology under " + root + " names this library"));
        foreach (string ctech in users)
            walks.Add(new ResolutionStepJson("named by", full, ctech, "a .ctech's MaterialLibraries"));
    }

    private static void ExplainLayout(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        Workspace(path, walks);

        LayoutView? view = null;
        try { view = LayoutPersistence.LoadFromFile(full); }
        catch (Exception ex) { JsonRun.Report(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }

        if (view is null) return;

        var (tech, ownCws) = TechnologyResolver.ResolveForDocument(
            view.TechRef, full, null, new TechnologyCache());

        // WHICH workspace resolved the technology, said separately from which workspace the document
        // belongs to. On a layout they are the same walk; on a `.cem` they are not, and reporting
        // them the same way in both places is what makes the difference visible.
        walks.Add(new ResolutionStepJson(
            "technology", view.TechRef ?? ownCws, tech.ResolvedPath,
            tech.Source switch
            {
                TechResolutionSource.LayoutRef        => "the layout's own TechRef, relative to its own directory",
                TechResolutionSource.WorkspaceDefault => "the workspace's DefaultTechRef — the layout states none",
                _                                     => "nothing resolved: no TechRef and no workspace default",
            }));

        foreach (var d in tech.Diagnostics)
            JsonRun.Report(CliDiagnostics.CheckResolverNote(path, d));
    }

    /// <summary>
    /// A <c>.cem</c>'s two walks, reported as two — which is the whole point of the option
    /// (<c>cli.md</c> §8.1). Resolution goes through <c>EmSetupResolver.Resolve</c> itself, so what
    /// is reported here is what <c>circuitrf em</c> actually used (Gate 5).
    /// </summary>
    private static int ExplainEmSetup(string path, List<ResolutionStepJson> walks, out ExplainEm3dJson? em3d)
    {
        em3d = null;
        string full = Path.GetFullPath(path);
        string? cws = Workspace(path, walks);

        EmSetup setup;
        try { setup = EmSetupPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }

        // brief-em3d-42 R-em3d42-5b — LayoutRef names the geometry document, a .clay or a .c3d.
        if (C3dSetups.IsThreeDView(EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)))
        {
            var src = Em3dSetupSource.FromCemOnThreeDView(full, setup, cws);
            walks.Add(new ResolutionStepJson("3D view", setup.LayoutRef, src.Resolution.LayoutPath,
                cws is null ? "relative to the .cem's own directory — no ancestor workspace"
                            : "relative to the .cem's ancestor workspace"));
            if (src.Elaboration is { } elaborated) ThreeDWalk(elaborated, walks);
            if (setup.Is3D) em3d = ExplainEm3d.Build(src);
            return src.Refusal is null ? 0 : 1;
        }

        var resolution = EmSetupResolver.Resolve(full, setup.LayoutRef, cws, new TechnologyCache());

        walks.Add(new ResolutionStepJson(
            "layout", setup.LayoutRef.Length > 0 ? setup.LayoutRef : null, resolution.LayoutPath,
            cws is null
                ? "relative to the .cem's own directory — no ancestor workspace"
                : "relative to the .cem's ancestor workspace"));

        // The technology walk starts from the LAYOUT, not from the .cem — so its own ancestor
        // workspace is the one that resolves it, and it may not be the one above.
        string? layoutCws = resolution.LayoutPath is { } lp ? DocumentKinds.AncestorCws(lp) : null;
        walks.Add(new ResolutionStepJson(
            "technology", layoutCws ?? resolution.LayoutPath, resolution.TechnologyPath,
            "resolved against the LAYOUT's own ancestor workspace, which need not be the .cem's"));

        foreach (var d in resolution.Diagnostics)
            JsonRun.Report(CliDiagnostics.CheckResolverNote(path, d));

        // The solve region is a decision the .cem makes about which geometry is solved at all, so it
        // is reported beside the walks that found that geometry. Only when one is set: a setup with
        // none solves the whole layout, as every .cem did before the region existed, and its explain
        // output stays exactly what it was.
        if (setup.SolveRegion is { } region)
            walks.Add(new ResolutionStepJson(
                "solve region", "this .cem's SolveRegion", region.Describe(),
                "only geometry inside the region reaches the extractors; shapes crossing its edge " +
                "are cut there and a via is kept or left out by its centre"));

        ExplainReturnPlane(setup, resolution, walks);

        // brief-em3d-5 R-em3d5-3: a 3D setup reports its problem IN ADDITION to the walks above,
        // generated through the one path `render` draws it through.
        if (setup.Is3D)
        {
            var source = Em3dSetupSource.From(full, setup, resolution);
            em3d = ExplainEm3d.Build(source);
            if (source.Refusal is not null && resolution.Source is not null) return 1;
        }

        return resolution.Source is null ? 1 : 0;
    }

    /// <summary>
    /// brief-em3d-42 R-em3d42-6 — <c>explain x.c3d</c>: the WALK — each instance's cell folder, view file and
    /// technology and where each came from, each unit conversion, each material merge or qualification, and
    /// the lowering table's choice per object — then, with exactly one embedded setup, the problem it makes.
    /// </summary>
    private static int ExplainThreeD(string path, List<ResolutionStepJson> walks, out ExplainEm3dJson? em3d,
                                     IReadOnlyList<(string Name, string Expr)> sets, string? setupName, string? objectName = null)
    {
        em3d = null;
        string full = Path.GetFullPath(path);
        Workspace(path, walks);
        NameWalk(full, sets, walks);
        Em3dSetupSource src;
        try { src = Em3dSetupSource.ForThreeDView(full, setupName); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }
        walks.Add(new ResolutionStepJson("technology", full, src.Elaboration?.TechnologyPath ?? src.Resolution.TechnologyPath,
            "the 3D view's own TechRef, else its ancestor workspace's default — resolved from the view's own path"));
        if (src.Elaboration is { } e)
        {
            ThreeDWalk(e, walks);
            // Resolved as the elaborator and check resolve it: a field written as an expression (a port's Rect) holds 0 in
            // its number until Resolve writes the value in, and the port walk measures those numbers.
            var doc = C3dPersistence.LoadFromFile(full);
            C3dResolver.Resolve(doc, C3dCell.Of(full));
            ModelWalk(doc, e, walks);
            PortWalk(doc, e, walks);
            ResultWalk(doc, full, e, walks);
        }
        if (objectName is not null)
        {
            if (src.Elaboration is not { } elaborated) return JsonRun.Fail(CliDiagnostics.ExplainObjectNotFound(objectName, path));
            if (!AppearanceWalk(elaborated, objectName, walks)) return JsonRun.Fail(CliDiagnostics.ExplainObjectNotFound(objectName, path));
            StepSolidsWalk(full, objectName, walks);
        }
        em3d = ExplainEm3d.Build(src);
        // brief-em3d-64 R-em3d64-6b — only a document that holds a kernel object asks the kernel anything.
        if (src.Elaboration is { KernelBuilds.Count: > 0 } withKernel)
            em3d = em3d with { GeometryKernel = ExplainEm3d.Kernel(withKernel) };
        return src.Refusal is null ? 0 : 1;
    }

    /// <summary>
    /// brief-em3d-51 R-em3d51-5c — per name of the 3D view's scope, where its value came from: an instance override does
    /// not exist at the top, so a parameter is its <c>.ccell</c> default, a linked VAR takes the parameter's, an unlinked
    /// one its own expression — and <c>--set</c> binds over all of them.
    /// </summary>
    private static void NameWalk(string full, IReadOnlyList<(string Name, string Expr)> sets, List<ResolutionStepJson> walks)
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch { return; }
        var cell = C3dCell.Of(full);
        var res = C3dResolver.Resolve(doc, cell, null, sets);
        foreach (var n in res.Names.Values)
        {
            string value = n.Error is { } err ? $"does not resolve: {err}"
                         : n.Value is { } v ? $"= {v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)} (base SI)"
                           + (n.Unit is { } u ? $"; unit {u}" : "") : "(not real)";
            string source = n.Source switch
            {
                C3dNameSource.Override    => "an instance override",
                C3dNameSource.CellDefault => $"the .ccell default '{n.Expression}'",
                C3dNameSource.LinkedVar   => $"a linked VAR: the cell parameter's {(n.ParameterSource == C3dNameSource.Override ? "override" : $".ccell default '{n.Expression}'")}",
                C3dNameSource.Var         => $"{(cell.Parameter(n.Name) is not null ? "an UNLINKED VAR, hiding the cell parameter" : "a VAR")} '{n.Expression}'",
                _                         => $"--set '{n.Expression}'",
            };
            int uses = res.Uses.TryGetValue(n.Name, out var list) ? list.Count : 0;
            // brief-em3d-132 R-em3d132-6 — which fields, by path; a point of a wire or a polyline as `point 2 z`.
            string which = uses == 0 ? "" : ": " + string.Join(", ", list!.Take(8).Select(u => $"'{u.Item}' {FieldLabel(doc, u.Item, u.Path)}"))
                                              + (uses > 8 ? $", and {uses - 8} more" : "");
            walks.Add(new ResolutionStepJson($"name {n.Name}", cell.CcellPath, $"{value}, from {source}; used by {uses} field(s){which}",
                "a 3D view sees its cell's parameters and its own VARs; a VAR named like a parameter is linked unless Linked is " +
                "false, and linked means the parameter's value, default included"));
        }
    }

    /// <summary>A field as check names it: its path, or a point's <c>point 2 z</c>.</summary>
    private static string FieldLabel(C3dDocument doc, string item, string path)
        => C3dBindings.ItemsOf(doc).FirstOrDefault(i => i.Name == item).Item is { } it && C3dBindings.Find(it, path) is { } f
            ? C3dBindings.Label(f.Spec, f.Component, path) : path;

    /// <summary>brief-em3d-51 — <c>explain x.c3d --expr</c>: the expression in the document's resolved scope.</summary>
    private static int ExplainThreeDExpression(string path, string expression, IReadOnlyList<(string Name, string Expr)> sets,
                                               out ExplainExpressionJson? value)
    {
        value = null;
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }
        foreach (var (name, e) in sets) Console.Error.WriteLine($"[circuitRF] set {name} = {e}");
        var res = C3dResolver.Resolve(doc, C3dCell.Of(full), null, sets);
        Value v;
        try { v = res.Evaluate(expression, null); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainExpressionFailed(expression, ex.Message)); }
        value = new ExplainExpressionJson(
            expression,
            v.Kind.ToString().ToLowerInvariant(),
            v.Kind == ValueKind.Real && res.SkipsSiteUnit(expression)
                ? $"{v} m ({C3dUnits.Spell(v.AsReal(), doc.DisplayUnit)})"
                : v.ToString(),
            v.Kind == ValueKind.Real    ? v.AsReal() : null,
            v.Kind == ValueKind.Complex ? [v.AsComplex().Real, v.AsComplex().Imaginary] : null,
            v.Kind == ValueKind.Bool    ? v.AsBool() : null);
        return 0;
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-5c — per port, the conductors each edge touches and why each end was chosen: the
    /// resolution a run makes (C3dPortReports), walked rather than restated.
    /// </summary>
    private static void PortWalk(C3dDocument doc, C3dElaboration e, List<ResolutionStepJson> walks)
    {
        foreach (var report in C3dPortReports.For(doc, e))
        {
            var r = report.Result;
            string touches = r.Contacts.Count == 0 ? "not measured: both ends are stated"
                : string.Join("; ", r.Contacts.Select(c => $"{c.Edge}: {(c.Objects.Count == 0 ? "nothing" : string.Join(", ", c.Objects.Select(o => $"'{o}'")))}"));
            string where = report.Setup is { } s ? $" (setup '{s}')" : "";
            if (r.Terminals is { } ts && r.Refusal is null)
            {
                TerminalWalk(doc, r, ts, where, touches, walks);
                continue;
            }
            walks.Add(new ResolutionStepJson($"port {r.Label}" + where, null,
                $"touches — {touches}. " + (r.Refusal ?? C3dPortReports.Describe(r)),
                "each edge's conductors to within 1 DBU; one opposite pair, one conductor each; the negative end is the " +
                "ground set's, else the larger surface; Flip swaps; Positive and Negative stated override it"));
        }
    }

    /// <summary>
    /// brief-em3d-114 R-em3d114-4 — a multi-terminal wave port: its face, its reference and why, then per terminal its number,
    /// conductor, Z0 and voltage path (ends in the display unit), and what its S-parameters are.
    /// </summary>
    private static void TerminalWalk(C3dDocument doc, C3dPortResult r, IReadOnlyList<Em3dPort> ts, string where, string touches,
                                     List<ResolutionStepJson> walks)
    {
        var first = ts[0];
        walks.Add(new ResolutionStepJson($"port {r.Label}" + where, null,
            $"touches — {touches}. A wave port with {ts.Count} terminals on the air box's {C3dPortReports.Em3dProblemFace(first)} face; " +
            $"the reference is '{first.NegativeObject}': {r.Reason}.",
            "the conductors meeting the region; the reference is the one in the ground set (a PEC air-box face counts), else " +
            "the largest surface; a tie is refused; Reference states it"));
        string P(Point3 q) => $"({C3dUnits.Spell(q.X, doc.DisplayUnit)}, {C3dUnits.Spell(q.Y, doc.DisplayUnit)}, {C3dUnits.Spell(q.Z, doc.DisplayUnit)})";
        foreach (var t in ts)
        {
            var v = t.VoltagePath!.Value;
            walks.Add(new ResolutionStepJson($"port {r.Label} terminal {t.Number}" + (t.SourceLabel is { } n ? $" '{n}'" : "") + where, null,
                $"conductor '{t.PositiveObject}', Z0 {C3dPorts.FormatZ0(t.Z0)} Ω, voltage path {P(v.From)} to {P(v.To)}",
                "from the reference's foot to the conductor's across their gap, at the conductor's centre; where the reference " +
                "encloses it, straight along the face's axes to the reference's nearest metal; VoltagePath states it; Flip reverses it"));
        }
        walks.Add(new ResolutionStepJson($"port {r.Label} result" + where, null,
            $"terminal S: each terminal is a port of the result (port{(ts.Count == 1 ? "" : "s")} {string.Join(", ", ts.Select(t => t.Number))}), " +
            "its voltage and current on its own conductor, against its own Z0",
            "a terminal is a port everywhere a port number is used: the .sNp, the port map, field drives"));
    }

    /// <summary>
    /// brief-em3d-87 R-em3d87-3 — the files a run of this view is solved from. brief-em3d-98 — per setup, whether its result
    /// is still the model's is the report's Solved section, from the one function the editor's glyphs read.
    /// </summary>
    private static void ResultWalk(C3dDocument doc, string full, C3dElaboration e, List<ResolutionStepJson> walks)
    {
        walks.Add(new ResolutionStepJson("inputs", full,
            $"{e.FilesRead.Count} file(s): {string.Join(", ", e.FilesRead.Select(Path.GetFileName))}",
            "every file the elaboration read — the view and its .ccell, each placed layout with its sub-cells and paired " +
            ".wBond, each nested 3D view, each technology and the material libraries it looks through; a run keeps their " +
            "hashes beside its result"));
        // brief-em3d-98 — whether each setup's result is still the model's is the Solved section (C3dSolveStatus), not a walk.
    }

    /// <summary>brief-em3d-93 R-em3d93-5 — what is drawn and left out of every run, as the run's own note says it
    /// (<see cref="C3dModelled.LeftOut"/>), and the ports and heat sources that are off.</summary>
    private static void ModelWalk(C3dDocument doc, C3dElaboration e, List<ResolutionStepJson> walks)
    {
        const string Rule = "each object's, instance's, port's and heat source's Model switch: drawn and editable, left out of every simulation run";
        if (C3dModelled.LeftOut(doc, e) is { } left) walks.Add(new ResolutionStepJson("not modelled", null, left, Rule));
        if (doc.Ports.Where(p => !p.Model).Select(C3dPorts.Label).ToList() is { Count: > 0 } ports)
            walks.Add(new ResolutionStepJson("ports not modelled", null,
                $"{string.Join(", ", ports)}: the result's ports are the other {doc.Ports.Count - ports.Count}, renumbered from 1", Rule));
        if (doc.HeatSources.Where(h => !h.Model).Select(h => h.Name).ToList() is { Count: > 0 } sources)
            walks.Add(new ResolutionStepJson("heat sources not modelled", null, $"{string.Join(", ", sources)}: no thermal run heats them", Rule));
    }

    /// <summary>
    /// brief-em3d-105 R-em3d105-7c — <c>explain x.c3d --object name</c>: the object's resolved appearance, one step per field,
    /// with the statement that decided it — through <see cref="CircuitRF.Render.Scene3D.Scene3DBuilder.AppearanceOf"/>, so the
    /// answer is the 3D view's own. <paramref name="name"/> is an elaborated name (<c>U1/trace</c>) or a top-level object's,
    /// which answers for every solid it elaborated to. False when it names nothing.
    /// </summary>
    /// <summary>
    /// brief-em3d-129 R-em3d129-1b — a Step object's solids, as the kernel reads its copied file: how many the part holds, and
    /// each one's colour, face count and volume. How an agent, which edits by writing the document, learns what to write: the
    /// pieces are <c>Solid = 1..n</c>, and moving the face references onto them is its own work (<c>check</c> names any that
    /// no longer lands).
    /// </summary>
    private static void StepSolidsWalk(string full, string name, List<ResolutionStepJson> walks)
    {
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch { return; }
        if (doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<CircuitRF.Design.ThreeD.C3dStep>()
                .FirstOrDefault(s => s.Name == name) is not { } step) return;
        CircuitRF.Design.ThreeD.Occ.GeometryKernelImportPart? part;
        try { part = CircuitRF.Design.ThreeD.Step.StepSplit.PartOf(step, full, CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared); }
        catch (CircuitRF.Design.ThreeD.Occ.GeometryKernelException ex)
        {
            walks.Add(new ResolutionStepJson("solids", $"{step.File} part {step.Part}", null, ex.Message));
            return;
        }
        if (part is null)
        {
            walks.Add(new ResolutionStepJson("solids", $"{step.File} part {step.Part}", null, "the file is not there, or holds no such part"));
            return;
        }
        walks.Add(new ResolutionStepJson("solids", $"{step.File} part {step.Part}", part.Solids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            step.Solid is { } k ? $"the part's solids, as the kernel reads the copied file; '{name}' is solid {k}"
            : part.Solids.Count > 1 ? $"the part's solids, as the kernel reads the copied file; '{name}' is all of them in one object, " +
                                      "so they share one material. Split into Solids, or write one Step object per solid (Solid = 1..n)"
            : "the part's solids, as the kernel reads the copied file"));
        foreach (var s in part.Solids)
            walks.Add(new ResolutionStepJson($"solid {s.Index}", s.Name.Length > 0 ? s.Name : null,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"colour {CircuitRF.Design.ThreeD.Step.StepSplit.ColourWord(s)}, {s.Faces} faces, volume {s.VolumeUm3 * 1e-18:G6} m³"),
                s.Closed ? "a closed solid" : $"not a closed solid: {s.Why}"));
    }

    private static bool AppearanceWalk(C3dElaboration e, string name, List<ResolutionStepJson> walks)
    {
        var problem = C3dProblemAssembly.ViewProblem([.. e.Solids, .. e.UnassignedSolids], [.. e.Sheets, .. e.UnassignedSheets], e.Materials, [],
                                                     C3dProblemAssembly.ExtentBox((0, 0, 0, 1, 1, 1)));
        var names = problem.Solids.Select(s => s.Name).Concat(problem.Sheets.Select(s => s.Name))
                           .Where(n => n == name || e.Provenance.TryGetValue(n, out var p) && p.TopObject == name).ToList();
        var looks = CircuitRF.Design.ThreeD.Appearance.AppearanceOverride.Of(e.Provenance);
        const string Rule = "object, instances innermost first, material (each with its Like), the material's Color, the role's " +
                            "default — drawing only, nothing read from εr or σ";
        bool any = false;
        foreach (string n in names)
        {
            if (CircuitRF.Render.Scene3D.Scene3DBuilder.AppearanceOf(problem, n, e.Origins, e.Technology, looks(n)) is not { } a) continue;
            any = true;
            var v = a.Values;
            foreach (var (field, value) in new (string, string)[]
                     {
                         ("BaseColor", Linear(v.BaseColor)), ("Metallic", Num(v.Metallic)), ("Roughness", Num(v.Roughness)),
                         ("Transmission", Num(v.Transmission)), ("Ior", Num(v.Ior)), ("Clearcoat", Num(v.Clearcoat)),
                         ("ClearcoatRoughness", Num(v.ClearcoatRoughness)), ("AttenuationColor", Linear(v.AttenuationColor)),
                         ("AttenuationDistance", double.IsPositiveInfinity(v.AttenuationDistance) ? "none (no tint)" : Num(v.AttenuationDistance) + " m"),
                     })
                walks.Add(new ResolutionStepJson($"appearance {n} {field}", a.Provenance.GetValueOrDefault(field),
                                                 $"{value} — from {a.Provenance.GetValueOrDefault(field)}", Rule));
        }
        return any;

        static string Num(double d) => d.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
        static string Linear(CircuitRF.Design.ThreeD.Appearance.AppearanceColour c) => $"({Num(c.R)}, {Num(c.G)}, {Num(c.B)}) linear";
    }

    private static void ThreeDWalk(C3dElaboration e, List<ResolutionStepJson> walks)
    {
        foreach (var s in e.WalkInstances)
            walks.Add(new ResolutionStepJson($"instance {s.Subject}", null, s.Detail,
                "the cell reference (ws:// through the workspace's alias; ExternalWorkspaceGate does not apply inside a " +
                "3D view), the cell's primary view, and that view's own technology"));
        foreach (var s in e.WalkUnits)
            walks.Add(new ResolutionStepJson($"units {s.Subject}", null, s.Detail,
                "each document in its own DbuPerMicron; nothing is rounded to a parent's DBU"));
        foreach (var s in e.WalkMaterials)
            walks.Add(new ResolutionStepJson($"material {s.Subject}", null, s.Detail,
                "resolved in each object's own document's technology; equal values merge, different ones are qualified"));
        foreach (var s in e.WalkLowering)
            walks.Add(new ResolutionStepJson($"lowered {s.Subject}", null, s.Detail,
                "the richest neutral primitive that states it exactly (box, extruded polygon, cylinder, else polyhedron)"));
    }

    /// <summary>
    /// <b>R-rp1-8 — which conductor every port in this run returns through, and who decided.</b>
    ///
    /// <para>This is the headless half of the run's own "Every port returns through …" note, and
    /// before RP-1 the answer had no spelling outside a full solve: the panel's own Ground-reference
    /// row is bound to the CROSS-SECTION readback, which a full-wave run never produces.</para>
    ///
    /// <para><b>It runs the EXTRACTION, not the analysis</b> — geometry and a stackup in, a medium
    /// out, no solve — and reports what the extraction resolved rather than a transcription of
    /// R-em-4. That distinction is the whole point: a rule restated here is a rule that can disagree
    /// with the run, which is precisely what a caller asks this verb to rule out. It is also the
    /// same call <c>EmRunService</c> makes, through the same flatten, so the levels it resolves are
    /// the run's levels — and the return plane depends on them.</para>
    ///
    /// <para>Silent when the extraction refuses: the refusal is a <c>check</c> answer, and repeating
    /// it here as a resolution step would report a plane the run does not have.</para>
    /// </summary>
    private static void ExplainReturnPlane(
        EmSetup setup, EmSetupResolution resolution, List<ResolutionStepJson> walks)
    {
        if (resolution.Source is not { Technology: { } tech } source) return;

        string from = setup.GroundStackupLayerName is { Length: > 0 } named
            ? $"this .cem's return plane: '{named}'"
            : $"technology '{tech.Name}' ground designations";

        var geometry = EmGeometry.ForSetup(setup, source);
        if (geometry.RegionRefusal is not null) return;
        var planar   = PlanarExtractor.Extract(
            geometry.Shapes, tech, source.DbuPerMicron, 0,
            setup.ToExtractionSettings(setup.LayoutRef), geometry.GeneratorIds);

        if (planar.ReturnPlane is not { } rp) return;

        string why = rp.Overridden
            ? "named by this EM setup, overriding R-em-4's inferred choice"
            : "R-em-4: the top surface of the highest ground-designated conductor below the " +
              "lowest analysis level";
        if (rp.Flipped)
            why += " — resolved with the stackup MIRRORED, because the plane lies above the " +
                   "analysis levels as the technology lists them; the height is measured downward " +
                   "from the top surface of the stackup, not up from its bottom";

        walks.Add(new ResolutionStepJson(
            "return plane", from,
            $"{rp.ConductorName ?? "Stackup.Bottom = Ground"} at {rp.TopM * 1e6:G4} µm" +
            (rp.Flipped ? " (flipped stack)" : ""),
            why));

        ExplainPortReturns(setup, source, planar.Problem!, walks);
    }

    /// <summary>
    /// <b>RP-2b/R-rp2b-9 — each port's OWN return, once a port in this run has one.</b>
    ///
    /// <para>The plane above is still the medium's boundary condition and still the return for every
    /// port that did not say otherwise. But since RP-2a a port may be two cuts driven against each
    /// other — a signal cut and a return cut in DRAWN metal — and the step above cannot express
    /// that, so a mixed run reported through it alone would say the plane was the negative terminal
    /// of a port for which it is nowhere in the loop.</para>
    ///
    /// <para><b>It reports what the EXTRACTION resolved, not what the label asked for.</b> The
    /// reference kind, the level and the point all come off the resolved <c>PlanarPort</c> — the
    /// same object <c>circuitrf em</c> hands the kernel — so this cannot drift from the run by
    /// restating a rule. A port the extraction refused is reported with its refusal rather than with
    /// an answer, because "this port would have returned through X" is not true of a port that does
    /// not resolve.</para>
    ///
    /// <para><b>Silent when every port returns through the plane</b>, which keeps an ordinary board's
    /// <c>explain</c> exactly as long as it was: the plane step above already answers the question
    /// completely for such a run, and N rows all saying "the plane" would bury the one row that ever
    /// differs. The moment ONE port names its own return, EVERY port gets a row — the interesting
    /// question about a mixed run is which ports are which, and half an answer is worse here than
    /// none.</para>
    /// </summary>
    private static void ExplainPortReturns(
        EmSetup setup, EmLayoutSource source, PlanarProblem problem, List<ResolutionStepJson> walks)
    {
        // A .cem written before the port TYPE moved onto the label still carries one; apply it in
        // memory, exactly as EmRunService does, so `explain` reports what a run would do. Writes
        // nothing — this verb is read-only by contract.
        EmPortKindMigration.ApplyInMemory(source.View.Shapes, setup.PortKinds);

        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology), source.Technology);

        if (!ports.Rows.Any(r => r.Port?.IsConductorReferenced == true)) return;

        foreach (var row in ports.Rows)
        {
            string step = $"port {row.Number} return";

            if (row.Port is not { } port)
            {
                walks.Add(new ResolutionStepJson(step, PortFrom(row.Label), null,
                    row.Problem ?? "this port did not resolve"));
                continue;
            }

            if (!port.IsConductorReferenced)
            {
                walks.Add(new ResolutionStepJson(step, PortFrom(row.Label),
                    "the run's return plane",
                    "this port names no return conductor, so its negative terminal is the plane"));
                continue;
            }

            int? level = port.NegativeLayerIndex ?? (problem.Layers.Count == 1 ? 0 : null);
            string conductor = level is { } li && li < problem.Layers.Count
                ? $"'{problem.Layers[li].Name}'"
                : "a meshed conductor";
            var neg = port.NegativeLocation!.Value;

            walks.Add(new ResolutionStepJson(step, PortFrom(row.Label),
                $"drawn metal on {conductor} at ({neg.X * 1e6:G6} µm, {neg.Y * 1e6:G6} µm)",
                port.Reference == PlanarPortReference.CoplanarGround
                    ? "this port names a coplanar ground conductor as its return: two cuts at one " +
                      "station, driven against each other, with the plane nowhere in the loop"
                    : "this port names a second conductor as its return: two cuts at one station, " +
                      "driven against each other, with the plane nowhere in the loop"));
        }
    }

    /// <summary>The label a port came from, as this walk's starting point — its own text when it has
    /// one, since that is what the user sees on the canvas and what the run's notes name it by.</summary>
    private static string PortFrom(LabelShape label)
        => label.Text is { Length: > 0 } t ? $"port label '{t}'" : "an unnamed port label";

    // ── --ref ────────────────────────────────────────────────────────────────

    private static (ExplainReferenceJson?, int) ExplainReference(string path, string reference)
    {
        // A reference is relative to the DOCUMENT'S OWN directory, which for a cell folder or a
        // workspace is the folder itself. Anything else would answer a question about a different
        // base than the one the file's references are written against.
        string from = Directory.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path))!;

        var res = CellSymbolResolver.Resolve(reference, from);
        string? resolved = ExternalCellRef.ResolveCellDir(reference, from);

        // A reference naming a FILE — a recognised schematic's source .clay (brief-artsch-7 R-as7-8) — resolves to
        // that file. Asked only after the cell walk found nothing, so a cell reference reads as it always did.
        if (res.State == CellSymbolState.NotFound && Path.HasExtension(reference)
            && Path.GetFullPath(Path.Combine(from, reference)) is var file && File.Exists(file))
        {
            string? root = WorkspaceRootFinder.FindAncestorCws(from) is { } ws ? Path.GetDirectoryName(ws) : null;
            return (new ExplainReferenceJson(reference, from, file, "resolved",
                root is null ? null : WorkspaceRootFinder.IsOutside(file, root), null), 0);
        }

        string? workspaceRoot = WorkspaceRootFinder.FindAncestorCws(from) is { } cws
            ? Path.GetDirectoryName(cws)
            : null;

        bool? outside = resolved is not null && workspaceRoot is not null
            ? WorkspaceRootFinder.IsOutside(resolved, workspaceRoot)
            : null;

        string state = res.State switch
        {
            CellSymbolState.Resolved       => "resolved",
            CellSymbolState.PrimaryMissing => "primary-missing",
            _                              => "not-found",
        };

        int exit = 0;
        if (res.State == CellSymbolState.NotFound)
            exit = JsonRun.Fail(CliDiagnostics.ExplainRefNotFound(reference, from));
        else if (res.State == CellSymbolState.PrimaryMissing)
            exit = JsonRun.Fail(CliDiagnostics.ExplainRefPrimaryMissing(reference, resolved ?? "?"));

        return (new ExplainReferenceJson(
            reference, from, resolved, state, outside, res.Redirect?.To), exit);
    }

    // ── --analysis ───────────────────────────────────────────────────────────

    private static (IReadOnlyList<ExplainAnalysisJson>, int) ExplainAnalyses(
        Library lib, TestBench tb, string? requested)
    {
        int exit = 0;

        // brief-tuneopt-11 R-to11-7: which chains an optimization evaluates under each analyses= scope,
        // through the run's own rule (OptimizationRun.AnalysesUnder) rather than a restatement of it.
        IReadOnlyList<string>? underGoals = null, underAll = null, optimizeAt = null;
        string setupScope = "";
        if (tb.Tuning is { } tuning && tuning.Goals.Any(g => g.Enabled))
        {
            underGoals = Design.Optimization.OptimizationRun.AnalysesUnder(tb, tuning, OptimizerScope.GoalAnalyses);
            underAll   = Design.Optimization.OptimizationRun.AnalysesUnder(tb, tuning, OptimizerScope.All);
            setupScope = tuning.Optimizer?.Scope == OptimizerScope.All ? "setup=all" : "setup=goals";
            // brief-yield-7 R-ya7-5: what one optimizer point costs across corners.
            var at = Design.Optimization.OptimizationRun.EvaluationPointsOf(tuning);
            if (at.Count != 1 || at[0] != Design.Statistics.CornerRun.NominalName) optimizeAt = at;
        }
        IReadOnlyList<string>? OptimizeScopes(string name) => underGoals is null ? null :
        [
            .. underGoals.Contains(name, StringComparer.Ordinal) ? new[] { "goals" } : [],
            .. underAll!.Contains(name, StringComparer.Ordinal) ? new[] { "all" } : [],
            setupScope,
        ];

        // R-wsp1-12(b): an S-parameter analysis reports its port count and its WSProbes — label,
        // idx and BOTH terminal nets — which only an elaboration can answer (idx is assigned there,
        // and a probe in a sub-cell is X1.GATE). Elaborated once, only when an S-parameter analysis
        // is declared, and a failure to elaborate leaves the rows as they were: this is a report of
        // what resolved, and elaboration failures are `check`'s to report.
        int? ports = null;
        IReadOnlyList<ExplainWsProbeJson>? wsProbes = null;
        // brief-wsprobe-6 §2: "explain --analysis lists the passivation each instance will use,
        // which is the way to see a refusal coming without running". One survey per S-parameter
        // analysis, because the two passivation lists are the directive's own.
        var ndfByAnalysis = new Dictionary<string, ExplainNdfJson>(StringComparer.Ordinal);
        if (tb.Analyses.Any(a => a is SParameterAnalysis))
        {
            try
            {
                using var nl = new Elaborator(lib).Elaborate(tb);
                ports = nl.Components.Count(ec =>
                    (ec.Model is PortModel or TermModel or P1ToneModel) && !ec.InstancePath.Contains('.'));
                if (nl.WspProbes.Count > 0)
                    wsProbes = nl.WspProbes.Select(w =>
                    {
                        var ec = nl.Components[w.ComponentIndex];
                        return new ExplainWsProbeJson(w.Label, w.Idx, NetName(nl, ec.Nodes[0]), NetName(nl, ec.Nodes[1]));
                    }).ToList();

                foreach (var spa in tb.Analyses.OfType<SParameterAnalysis>())
                    ndfByAnalysis[spa.Name] = SurveyNdf(spa, nl);
            }
            catch (Exception) { /* reported by check; nothing to explain here */ }
        }

        if (requested is not null
            && !tb.Analyses.Any(a => a.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)))
        {
            exit = JsonRun.Fail(CliDiagnostics.ExplainAnalysisNotFound(
                requested,
                tb.Analyses.Count > 0 ? string.Join(", ", tb.Analyses.Select(a => a.Name)) : "(none)"));
        }

        // One selection per kind, through the function the run verbs select with. A netlist can
        // declare an HB chain AND a loadpull chain, and each verb dispatches its own — so
        // "dispatched" is per verb rather than a single winner.
        var dispatched = new Dictionary<string, string>(StringComparer.Ordinal);   // analysis → verb
        var promoted   = new Dictionary<string, string>(StringComparer.Ordinal);   // analysis → promoted-from
        var reasons    = new List<string>();

        foreach (var (isBase, label, hint, verb) in AnalysisKinds)
        {
            // A kind the netlist declares NONE of is not a finding — chain selection is per kind, so
            // a bench declaring only an S-parameter sweep genuinely has no HB chain and saying so
            // three times would warn about every S-parameter and DC document there is.
            if (!tb.Analyses.Any(isBase)) continue;

            var sel = ChainSelector.Select(tb, requested, isBase, label, hint);

            // A NAMED analysis comes back from chain selection even when it is not of this verb's
            // kind — `SelectTop` hands back `owner ?? named`, so `lp -a HB1` returns HB1. Reporting
            // that as "lp dispatches HB1" would be a claim about a run that cannot happen, so the
            // selection is counted only when the chain it picked bottoms out in this verb's own
            // base analysis. The refusal a caller would actually get is `lp`'s own.
            if (sel.Selected is { } top
                && ChainSelector.BaseOfChain(top, tb) is { } base_ && isBase(base_))
            {
                dispatched[top.Name] = verb;
                if (sel.PromotedFrom is { } from) promoted[top.Name] = from.Name;
            }
            else if (sel.Why is { } why) reasons.Add(why);
        }

        if (!CircuitSource.DeclaresARunnableAnalysis(tb))
            JsonRun.Report(CliDiagnostics.ExplainNoRunnableAnalysis("The document declares no analysis."));
        else if (reasons.Count > 0)
            JsonRun.Report(CliDiagnostics.ExplainNoRunnableAnalysis(string.Join(" ", reasons)));

        var inner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.InnerAnalysisName))
                inner.Add(ps.InnerAnalysisName);

        var rows = new List<ExplainAnalysisJson>(tb.Analyses.Count);
        foreach (var a in tb.Analyses)
        {
            var chain = new List<string>();
            Analysis? walk = a;
            for (int guard = 0; walk is not null && guard < 64; guard++)
            {
                chain.Add(walk.Name);
                if (walk is not ParametricSweepAnalysis ps) break;
                walk = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
            }

            var top = AnalysisChain.ResolveEffectiveTop(a, tb);

            // R-aut8-8. "Runnable" is the chain reaching an enabled analysis AND every reference the
            // chain names by string resolving. Checked over the whole chain, not just this root: a
            // parametric sweep is runnable exactly when the analysis it wraps is.
            var unresolved = UnresolvedReferences(a, tb);
            bool runnable  = top is not null
                          && AnalysisChain.IsChainRunnable(top, tb)
                          && unresolved.Count == 0;

            bool isSparam = a is SParameterAnalysis;
            rows.Add(new ExplainAnalysisJson(
                a.Name,
                KindOf(a),
                a.Enabled,
                runnable,
                !inner.Contains(a.Name),
                chain,
                dispatched.ContainsKey(a.Name) && runnable,
                dispatched.TryGetValue(a.Name, out var byVerb) && runnable ? byVerb : null,
                promoted.TryGetValue(a.Name, out var from) ? from : null,
                SweepOf(a),
                unresolved.Count > 0 ? unresolved : null,
                Ports:    isSparam ? ports : null,
                WsProbes: isSparam ? wsProbes : null,
                MarginThreshold: MarginThresholdOf(a),
                Ndf: isSparam && ndfByAnalysis.TryGetValue(a.Name, out var ndfRow) ? ndfRow : null,
                Optimize: OptimizeScopes(a.Name),
                OptimizeAt: OptimizeScopes(a.Name) is { Count: > 1 } ? optimizeAt : null));
        }

        return (rows, exit);
    }

    /// <summary>
    /// What every placed component's passivation would do under this analysis' own <c>PassiveVars</c>
    /// and <c>PassiveParams</c>, and the refusal the run would raise if it would raise one
    /// (brief-wsprobe-6 §2). Reported whether or not <c>NDF=yes</c> is actually set, because
    /// "could this design yield an NDF at all" is worth answering before the knob is turned on.
    ///
    /// <para>Nothing here solves or stamps: it is the same <c>NdfPassivation.Survey</c> the engine
    /// calls before its first factorisation, so a refusal seen here is the refusal a run raises,
    /// word for word — a listing built independently would eventually disagree with it.</para>
    /// </summary>
    private static ExplainNdfJson SurveyNdf(SParameterAnalysis spa, ElaboratedNetlist nl)
    {
        double[] freqs;
        try { freqs = spa.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit); }
        catch (Exception) { freqs = []; }

        var survey = NdfPassivation.Survey(
            nl, freqs,
            NdfPassivation.ParseList(spa.PassiveVarsExpr),
            NdfPassivation.ParseList(spa.PassiveParamsExpr));

        return new ExplainNdfJson(
            Analysis.ParseNdf(spa.NdfExpr),
            survey.Refusal,
            [.. survey.Instances.Select(i => new ExplainNdfInstanceJson(
                i.InstancePath, i.ComponentType, ActivityWord(i.Activity), i.Passivation, i.Refusal))]);
    }

    /// <summary>The enum, in the JSON's own camelCase — a stable token a caller can branch on.</summary>
    private static string ActivityWord(CircuitRF.Core.Activity a) => a switch
    {
        CircuitRF.Core.Activity.Passive          => "passive",
        CircuitRF.Core.Activity.ActiveExact      => "activeExact",
        CircuitRF.Core.Activity.ActiveUserScaled => "activeUserScaled",
        _                                        => "blackBox",
    };

    /// <summary>R-wsp9-4: the effective <c>MarginThreshold=</c> of an analysis that reads one, as
    /// the number in dB or the word <c>none</c>. Null for a kind that has no such key, so the field
    /// is absent rather than reported as a default nothing consults.</summary>
    private static string? MarginThresholdOf(Analysis a)
    {
        string? expr = a switch
        {
            SParameterAnalysis sp      => sp.MarginThresholdExpr,
            HarmonicBalanceAnalysis hb => hb.MarginThresholdExpr,
            _                          => null,
        };
        if (expr is null) return null;
        var db = Analysis.ParseMarginThresholdDb(expr);
        return db is null
            ? Analysis.MarginThresholdNone
            : db.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NetName(ElaboratedNetlist nl, int node)
        => node == 0 ? "0" : node < nl.Nodes.Count ? nl.Nodes.NameOf(node) : $"node {node}";

    /// <summary>
    /// The references an analysis names as STRINGS and that do not resolve in this design — the load
    /// and source tuner instance names, the inner analysis a sweep wraps, and the variables a tone
    /// expression reads.
    ///
    /// <para><b>Why <c>explain</c> asks this at all (AUT-8 R-aut8-8).</b> A DUT cell was reported
    /// <c>runnable: true, dispatched: true, dispatchedBy: lpp</c> while naming a load tuner and a
    /// source tuner that do not exist in it, containing no bias source, and referencing an undefined
    /// variable. Whether a thing will run is the question this verb exists to answer.</para>
    ///
    /// <para><b>Within R-aut4-1's budget.</b> Each of these is a name lookup against a list already in
    /// memory — no elaboration, no solve. What is deliberately NOT checked is anything needing one:
    /// "contains no bias source" is a property of the SOLVED circuit, and claiming it here would be
    /// the same overreach in the other direction. So this reports what it can establish and the
    /// chain's own enabled-ness, and nothing it would have to guess at.</para>
    /// </summary>
    private static List<string> UnresolvedReferences(Analysis a, TestBench tb)
    {
        var bad = new List<string>();

        // Walk the chain: a sweep's own runnability is its inner analysis's.
        Analysis? walk = a;
        for (int guard = 0; walk is not null && guard < 64; guard++)
        {
            switch (walk)
            {
                case ParametricSweepAnalysis ps:
                    if (AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb) is null)
                        bad.Add($"Inner={ps.InnerAnalysisName}: no analysis of that name in this document.");
                    if (!string.IsNullOrEmpty(ps.SweepVarName) && !DeclaresVariable(tb, ps.SweepVarName))
                        bad.Add($"Var={ps.SweepVarName}: no global variable of that name.");
                    walk = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);
                    continue;

                case LoadpullAnalysis lp:
                    CheckTuner(bad, tb, "LoadTuner",   lp.LoadTunerName);
                    CheckTuner(bad, tb, "SourceTuner", lp.SourceTunerName);
                    CheckTone (bad, tb, lp.ToneExpr);
                    break;

                case LoadpullPursuitAnalysis lpp:
                    CheckTuner(bad, tb, "LoadTuner",   lpp.LoadTunerName);
                    CheckTuner(bad, tb, "SourceTuner", lpp.SourceTunerName);
                    CheckTone (bad, tb, lpp.ToneExpr);
                    break;

                case HarmonicBalanceAnalysis hb:
                    if (hb.ToneExprs.Length > 0)
                        foreach (var t in hb.ToneExprs) CheckTone(bad, tb, t);
                    else
                        CheckTone(bad, tb, hb.ToneExpr);
                    break;
            }
            break;
        }

        return bad;
    }

    /// <summary>A tuner is named by INSTANCE name and must be a Tuner in the bench itself — a loadpull
    /// tunes the top-level terminations, so one inside a sub-cell is not reachable by this name.</summary>
    private static void CheckTuner(List<string> bad, TestBench tb, string key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { bad.Add($"{key}= is empty."); return; }

        var hit = tb.Instances.FirstOrDefault(
            i => i.InstanceName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            var tuners = tb.Instances
                .Where(i => i.Reference.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.InstanceName).ToList();
            bad.Add($"{key}={name}: no instance of that name in this document." +
                    (tuners.Count > 0 ? $" Its Tuners are: {string.Join(", ", tuners)}." : " It has no Tuner."));
        }
        else if (!hit.Reference.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
            bad.Add($"{key}={name}: that instance is a {hit.Reference}, not a Tuner.");
    }

    /// <summary>
    /// Every bare name a tone expression reads must be a declared global. A tone that silently
    /// resolves to nothing is what made a run announce <c>f0=0 GHz</c> and proceed.
    /// </summary>
    private static void CheckTone(List<string> bad, TestBench tb, string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return;

        IReadOnlyCollection<string> names;
        try   { names = AstWalker.CollectRefs(Parser.Parse(expr)); }
        catch (ExpressionException ex) { bad.Add($"Tone={expr}: {ex.Message}"); return; }

        foreach (var n in names)
            if (!DeclaresVariable(tb, n))
                bad.Add($"Tone={expr}: '{n}' is not a variable this document declares.");
    }

    private static bool DeclaresVariable(TestBench tb, string name)
        => tb.GlobalVariables.Any(v => v.Name.Equals(name, StringComparison.Ordinal));

    private static string KindOf(Analysis a) => a switch
    {
        DcAnalysis               => "dc",
        SParameterAnalysis       => "sparam",
        HarmonicBalanceAnalysis  => "hb",
        LoadpullAnalysis         => "loadpull",
        LoadpullPursuitAnalysis  => "loadpull-pursuit",
        ParametricSweepAnalysis  => "parametric-sweep",
        _                        => a.GetType().Name,
    };

    /// <summary>
    /// A sweep's resolved axis, in BASE SI, with the unit it was stated in and the scale that got it
    /// there (R-aut4-9). Reading a unit's mark without its scale has already produced a sweep that
    /// ran at 2 Hz and looked entirely normal, and that class of error is invisible without a run
    /// unless the numbers are shown next to both.
    ///
    /// <para><c>SweepValues</c> is the authority, not the spec — the spec's coefficients are in the
    /// stated unit and the array is what the engine actually steps through.</para>
    /// </summary>
    private static ExplainSweepJson? SweepOf(Analysis a)
    {
        if (a is not ParametricSweepAnalysis ps || ps.SweepValues.Length == 0) return null;

        string stated = ps.Spec?.Unit ?? "";
        double scale  = stated.Length == 0 ? 1.0 : Units.Scale(stated) ?? 1.0;

        // Only a step-SIZE sweep has a step. A point-count or explicit-list sweep does not, and a
        // step computed from the endpoints would be an invention — wrong outright on a log sweep.
        double? step = ps.Spec is { Mode: SweepAxisMode.StepSize } spec
            ? spec.StepOrCount * scale
            : null;

        return new ExplainSweepJson(
            ps.SweepVarName,
            ps.SweepValues.Length,
            ps.SweepValues[0],
            ps.SweepValues[^1],
            step,
            stated.Length == 0 ? "" : Units.BaseUnit(stated),
            stated,
            scale,
            (ps.Spec?.Kind ?? SweepKind.Linear).ToString().ToLowerInvariant());
    }

    // ── --expr ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates in the design's OWN resolved scope, through the one expression engine — never by
    /// substitution (<c>docs/design/expressions.md</c>). Elaboration runs first because that is what
    /// builds the scope, binds the ambient the design may not state, and applies every
    /// <c>--set</c>-style replacement that already landed in <c>GlobalVariables</c>.
    /// </summary>
    private static int ExplainExpression(
        Library lib, TestBench tb, string expression, out ExplainExpressionJson? value)
    {
        value = null;

        var elaborator = new Elaborator(lib);
        ElaboratedNetlist nl;
        try { nl = elaborator.Elaborate(tb); }
        catch (Exception ex)
        { return JsonRun.Fail(CliDiagnostics.ExplainExpressionFailed(expression, ex.Message)); }

        using (nl)
        {
            Value v;
            try { v = elaborator.EvaluateInGlobalScope(expression); }
            catch (Exception ex)
            { return JsonRun.Fail(CliDiagnostics.ExplainExpressionFailed(expression, ex.Message)); }

            value = new ExplainExpressionJson(
                expression,
                v.Kind.ToString().ToLowerInvariant(),
                v.ToString(),
                v.Kind == ValueKind.Real    ? v.AsReal() : null,
                v.Kind == ValueKind.Complex ? [v.AsComplex().Real, v.AsComplex().Imaginary] : null,
                v.Kind == ValueKind.Bool    ? v.AsBool() : null);
        }

        return 0;
    }

    // ── reading the circuit ──────────────────────────────────────────────────

    /// <summary>
    /// The circuit a document holds, read the way the application reads it — including a `.csch`'s
    /// `.cnl` round trip, which is not cosmetic (see <see cref="CircuitSource"/>). Reported through
    /// this verb's own diagnostic when the read fails.
    /// </summary>
    /// <summary>
    /// brief-artsch-2 R-as2-4: a TLIN stated by its physical length reports the electrical length it
    /// resolved to — the number a designer used to type, and the one a schematic drawn from artwork
    /// never shows. Only when such a line is there, because it costs an elaboration; one that fails is
    /// <c>check</c>'s to report, so nothing is said here.
    /// </summary>
    private static void PhysicalLines(string path, DocumentKind kind, List<ResolutionStepJson> walks)
    {
        if (CircuitSource.Read(path, kind) is not var (lib, tb)) return;
        bool any = tb.Instances.Concat(lib.Cells.SelectMany(c => c.Instances))
            .Any(i => i.Reference.Equals("TLIN", StringComparison.OrdinalIgnoreCase) && i.Overrides.Any(o => o.Name == "L"));
        if (!any) return;

        ElaboratedNetlist nl;
        try { nl = new Elaborator(lib).Elaborate(tb); }
        catch (Exception) { return; }

        foreach (var c in nl.Components)
        {
            if (c.Model is not TLineModel { IsPhysical: true } line) continue;
            double f = line.ReferenceFrequencyHz > 0 ? line.ReferenceFrequencyHz : 1e9;
            double deg = line.ElectricalLengthRad(f) * 180.0 / Math.PI;
            walks.Add(new ResolutionStepJson($"TLIN {c.InstancePath}", path,
                $"θ = {deg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}° at {Check.Hz(f)}",
                $"2π·F·L·√Eeff/c₀ with L = {(line.LengthMeters * 1e3).ToString("G6", System.Globalization.CultureInfo.InvariantCulture)} mm, "
              + $"Eeff = {line.Eeff.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}"
              + (line.ReferenceFrequencyHz > 0 ? "" : "; the line states no F, so at 1 GHz")));
        }
    }

    private static (Library Lib, TestBench Tb)? ReadCircuit(string path, DocumentKind kind)
        => CircuitSource.Read(path, kind,
               message => JsonRun.Report(CliDiagnostics.ExplainUnreadable(path, message)));

    // ── the human report ─────────────────────────────────────────────────────

    private static void Print(
        string path, DocumentKind kind,
        IReadOnlyList<ResolutionStepJson> walks,
        IReadOnlyList<ExplainAnalysisJson>? analyses,
        ExplainExpressionJson? value,
        ExplainReferenceJson? reference,
        IReadOnlyList<ExplainCellJson>? cells = null,
        ExplainLayersJson? layers = null,
        ExplainExtentsJson? extents = null,
        IReadOnlyList<ExplainFootprintJson>? footprints = null)
    {
        Console.WriteLine($"{path}  ({DocumentKinds.Name(kind)})");

        foreach (var w in walks)
        {
            Console.WriteLine($"  {w.Step,-12} {w.Resolved ?? "(nothing)"}");
            // The RULE, always — the walk is the answer as much as the destination is (R-aut4-7),
            // and it is the half a caller cannot reconstruct from the path alone.
            Console.WriteLine($"  {"",-12} via {w.How}");
        }

        if (reference is not null)
        {
            Console.WriteLine($"  reference    '{reference.Ref}' from {reference.From}");
            Console.WriteLine($"  {"",-12} {reference.State}" +
                              (reference.ResolvedPath is { } r ? $" → {r}" : "") +
                              (reference.OutsideWorkspace == true ? "  (outside its workspace)" : ""));
            if (reference.Redirect is { } moved)
                Console.WriteLine($"  {"",-12} via a recorded move to '{moved}'");
        }

        if (value is not null)
            Console.WriteLine($"  {value.Expression} = {value.Text}   ({value.Kind})");

        if (cells is not null)
        {
            Console.WriteLine($"  cells: {cells.Count}");
            foreach (var c in cells)
            {
                Console.WriteLine($"    {c.Name,-20} {c.Folder}"
                                  + (c.OutsideWorkspace == true ? "  (outside its workspace)" : "")
                                  + (c.Generated == true ? "  (generated)" : ""));
                foreach (var v in c.Views)
                {
                    // A view that does not exist is a row too — "this cell has no symbol" is an answer
                    // a caller composing with `render --view` needs, and an omitted row reads as an
                    // oversight rather than as a fact.
                    Console.WriteLine($"      {v.Type,-10} {v.State,-22} {v.Primary ?? "(none)"}"
                                      + (v.Candidates.Count > 1 ? $"   of {v.Candidates.Count}: {string.Join(", ", v.Candidates)}" : ""));
                    if (v.Defect is { } d) Console.WriteLine($"      {"",-10} defect: {d}");
                }
            }
        }

        if (layers is not null)
        {
            // No heading here: the `layers` walk step above already named the technology, where it
            // came from and how it was found. Repeating it would be the one thing R-aut4-7's walk
            // format exists to avoid — the same fact twice, in two wordings.
            foreach (var l in layers.Layers)
                Console.WriteLine(
                    $"    {l.Name,-20} {l.Number}/{l.Datatype,-6} {l.Color}  {l.Fill,-10}"
                    + (l.Visible ? "" : " hidden") + (l.Selectable ? "" : " locked")
                    + (l.Purpose is { } p ? $"  purpose={p}" : "")
                    + (l.Shapes is { } n ? $"   {n} shape(s)" : "")
                    + (l.InstancesUsing is > 0 and { } u ? $", {u} instance(s)" : ""));
            if (layers.Truncated == true)
                Console.WriteLine("    (counts are a floor — the hierarchy is larger than this walk)");
        }

        if (footprints is not null)
        {
            // WHAT IT STATES, WHAT THAT RESOLVED TO, HOW MANY PADS, and the walk that got there —
            // R-fp4-4b. The pad count is printed against the PORT count on the same line, because
            // the pair is the contract (§1f) and two numbers in two places is how a mismatch goes
            // unread.
            Console.WriteLine($"  footprints: {footprints.Count}");
            foreach (var fp in footprints)
            {
                Console.WriteLine($"    {fp.Component,-14} {fp.Stated,-22} {fp.State}"
                                  + $"   {fp.Pads} pad(s) / {fp.Ports} port(s)"
                                  + (fp.Pads >= 0 && fp.Pads != fp.Ports ? "   MISMATCH" : ""));
                Console.WriteLine($"      {"",-12} via {fp.Walk}");
                if (fp.ResolvedTo is { } to)  Console.WriteLine($"      {"",-12} → {to}");
                if (fp.Technology is { } tch) Console.WriteLine($"      {"",-12} against technology {tch}");
                if (fp.Refusal is { } why)    Console.WriteLine($"      {"",-12} {why}");
            }
        }

        if (extents is not null)
        {
            if (extents.Empty)
                Console.WriteLine($"  extents      (empty)   unit {extents.Unit}, scale {extents.Scale:G6}");
            else if (extents.Z0 is { } z0)
                Console.WriteLine(
                    $"  extents      {extents.X0:G6} {extents.Y0:G6} {z0:G6} .. {extents.X1:G6} {extents.Y1:G6} {extents.Z1:G6}"
                    + $"  ({extents.Width:G6} x {extents.Height:G6} x {extents.Depth:G6} {extents.Unit}, scale {extents.Scale:G6})");
            else
                Console.WriteLine(
                    $"  extents      {extents.X0:G6} {extents.Y0:G6} .. {extents.X1:G6} {extents.Y1:G6}"
                    + $"  ({extents.Width:G6} x {extents.Height:G6} {extents.Unit}, scale {extents.Scale:G6})");
            if (extents.Bounds is { } bounds)
                Console.WriteLine($"  {"",-12} in its display unit: {bounds}");
            // R-aut12-3: the same box in the spelling `render --window` takes, so the answer can be
            // pasted rather than converted. Printed on its own line and NAMED as the flag it feeds.
            if (extents.Window is { } win)
                Console.WriteLine($"  {"",-12} --window {win}");
            if (extents.Note is { } en) Console.WriteLine($"  {"",-12} note: {en}");
            foreach (var pl in extents.PerLayer ?? [])
                Console.WriteLine($"    {pl.Name,-20} {pl.X0:G6} {pl.Y0:G6} .. {pl.X1:G6} {pl.Y1:G6}"
                    + (pl.Window is { } lw ? $"   --window {lw}" : ""));
        }

        if (analyses is not null)
        {
            Console.WriteLine("  analyses:");
            foreach (var a in analyses)
            {
                string flags = string.Join(" ", new[]
                {
                    a.Enabled    ? "" : "disabled",
                    a.Runnable   ? "" : "not-runnable",
                    a.IsRoot     ? "" : "inner",
                    a.Dispatched ? $"DISPATCHED by {a.DispatchedBy}" : "",
                }.Where(s => s.Length > 0));

                Console.WriteLine($"    {a.Name,-16} {a.Kind,-18} {string.Join(" > ", a.Chain),-28} {flags}");

                if (a.PromotedFrom is { } from)
                    Console.WriteLine($"      promoted from '{from}' — naming the inner analysis would lose the sweep axis");

                // brief-tuneopt-11 R-to11-7. The last entry is the scope the optimize line chose.
                if (a.Optimize is { Count: > 0 } opt)
                {
                    var scopes = opt.Take(opt.Count - 1).ToList();
                    Console.WriteLine(scopes.Count == 0
                        ? $"      optimize: not run under analyses=goals or all ({opt[^1]})"
                        : $"      optimize: run under analyses={string.Join(" and ", scopes)} ({opt[^1]})" +
                          (a.OptimizeAt is { } at ? $" at {string.Join(", ", at)} — {at.Count} evaluations per point" : ""));
                }

                if (a.Sweep is { } s)
                    Console.WriteLine(
                        $"      sweep {s.Variable}: {s.Start:G6} .. {s.Stop:G6}" +
                        (s.Step is { } st ? $" step {st:G6}" : "") +
                        $" {s.BaseUnit} ({s.Points} pts, {s.Kind}" +
                        (s.StatedUnit.Length > 0 ? $", stated in {s.StatedUnit} ×{s.Scale:G6}" : "") + ")");

                // R-aut8-8. "not-runnable" without WHICH reference failed leaves the caller no
                // better off than the optimistic "runnable" this replaced, so the reasons are on
                // stdout too rather than only in --json.
                foreach (var u in a.Unresolved ?? [])
                    Console.WriteLine($"      unresolved: {u}");

                // R-wsp1-12(b): what the S-parameter run will report — S over its ports, or none,
                // and the probes with the idx each will carry and the two nets each splits.
                if (a.WsProbes is { } probes)
                {
                    Console.WriteLine(a.Ports is > 0
                        ? $"      S-parameters: {a.Ports} port(s); WSProbe outputs: {probes.Count} probe(s)"
                        : $"      S-parameters: none (no ports); WSProbe outputs: {probes.Count} probe(s)");
                    foreach (var w in probes)
                        Console.WriteLine($"      WSProbe {w.Label} idx={w.Idx}  G={w.G}  L={w.L}");
                }

                // R-wsp9-4: the effective margin threshold beside the probe list. The default is
                // not written in the document, so it is reported rather than left to be assumed.
                if (a.MarginThreshold is { } mt)
                    Console.WriteLine(mt == "none"
                        ? "      MarginThreshold: none (the stability-margin report is off)"
                        : $"      MarginThreshold: {mt} dB");

                // brief-wsprobe-6 §2: the passivation each instance will use, which is the way to
                // see an NDF refusal coming without running. Reported whether or not NDF=yes is
                // set — "could this design yield an NDF at all" is worth answering before the knob
                // is turned on — but only the instances that are NOT plainly passive are listed,
                // because a hundred resistors saying "passive" is noise.
                if (a.Ndf is { } ndf)
                {
                    var interesting = ndf.Instances
                        .Where(i => i.Activity != "passive" || i.Refusal is not null)
                        .ToList();
                    Console.WriteLine(
                        $"      NDF: {(ndf.Enabled ? "yes" : "no")}" +
                        (ndf.Refusal is null
                            ? $"; {ndf.Instances.Count} instance(s), " +
                              $"{interesting.Count} carrying activity to passivate"
                            : "; WOULD BE REFUSED"));
                    foreach (var i in interesting)
                        Console.WriteLine(
                            $"      NDF {i.Instance} ({i.Type}) {i.Activity}: {i.Passivation}");
                    if (ndf.Refusal is { } why)
                        foreach (var line in why.Split('\n'))
                            Console.WriteLine($"      {line.TrimEnd()}");
                }
            }
        }
    }
}
