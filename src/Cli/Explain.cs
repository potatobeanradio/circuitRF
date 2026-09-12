using CircuitRF.Core.Devices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Stability;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
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
        string? path = null, expr = null, reference = null, analysisName = null;
        bool wantAnalyses = false, wantCells = false, wantLayers = false, wantExtents = false, all = false;
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
                  + (wantCells ? 1 : 0) + (wantLayers ? 1 : 0) + (wantExtents ? 1 : 0);
        if (asked > 1) { JsonRun.Report(CliDiagnostics.ExplainOneQuestion()); return Usage(); }
        if (all && !wantCells) { JsonRun.Report(CliDiagnostics.ExplainAllNeedsCells()); return Usage(); }

        if (!File.Exists(path) && !Directory.Exists(path))
            return JsonRun.Fail(CliDiagnostics.ExplainPathNotFound(path));

        var kind  = DocumentKinds.Classify(path);
        var walks = new List<ResolutionStepJson>();
        int exit  = 0;

        IReadOnlyList<ExplainAnalysisJson>? analyses = null;
        ExplainExpressionJson?              value    = null;
        ExplainReferenceJson?               refRes   = null;
        IReadOnlyList<ExplainCellJson>?     cells    = null;
        ExplainLayersJson?                  layers   = null;
        ExplainExtentsJson?                 extents  = null;

        // The document's OWN resolution always runs, whatever was asked: "which workspace, which
        // technology" is context for every other answer, and a report that omitted it would leave a
        // caller unable to tell which process an expression or a rule was read against.
        switch (kind)
        {
            case DocumentKind.Layout:   ExplainLayout(path, walks); break;
            case DocumentKind.EmSetup:  exit |= ExplainEmSetup(path, walks); break;
            case DocumentKind.Netlist:
            case DocumentKind.Schematic:
            case DocumentKind.Cell:
            case DocumentKind.Workspace:
            case DocumentKind.Technology:
            case DocumentKind.Symbol:
            case DocumentKind.AssemblyRules:
            case DocumentKind.Folder:
                // Nothing but the workspace walk to report: none of these resolves a second
                // reference of its own. Reported anyway, because "which workspace" is the context
                // every other answer is read against.
                Workspace(path, walks);
                break;
            case DocumentKind.Interchange:
                walks.Add(new ResolutionStepJson(
                    "format", path, DocumentKinds.InterchangeFormat(path),
                    "content and extension, through `convert`'s own classifier"));
                break;
            case DocumentKind.Touchstone:
                exit |= ExplainTouchstone(path, walks);
                break;
            default:
                return JsonRun.Fail(CliDiagnostics.ExplainUnknownKind(path));
        }

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
                }
                if (expr is not null) exit |= ExplainExpression(lib, tb, expr, out value);
            }
        }

        JsonRun.Explain = new ExplainReportJson(
            path, DocumentKinds.Name(kind), walks, analyses, value, refRes, cells, layers, extents);

        Print(path, kind, walks, analyses, value, refRes, cells, layers, extents);
        return exit;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf explain <path> [--expr \"<expression>\"] [--set var=expr]");
        Console.Error.WriteLine("                            [--analysis [<name>]] [--ref <relative-ref>]");
        Console.Error.WriteLine("                            [--cells [--all]] [--layers] [--extents] [--view <name>]");
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
    private static int ExplainEmSetup(string path, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        string? cws = Workspace(path, walks);

        EmSetup setup;
        try { setup = EmSetupPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }

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

        ExplainReturnPlane(setup, resolution, walks);

        return resolution.Source is null ? 1 : 0;
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

        var geometry = EmGeometry.Flatten(source.View, source.AbsolutePath);
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
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, problem, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit, setup.ResolvePortKind,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));

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
                Ndf: isSparam && ndfByAnalysis.TryGetValue(a.Name, out var ndfRow) ? ndfRow : null));
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
        ExplainExtentsJson? extents = null)
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

        if (extents is not null)
        {
            if (extents.Empty)
                Console.WriteLine($"  extents      (empty)   unit {extents.Unit}, scale {extents.Scale:G6}");
            else
                Console.WriteLine(
                    $"  extents      {extents.X0:G6} {extents.Y0:G6} .. {extents.X1:G6} {extents.Y1:G6}"
                    + $"  ({extents.Width:G6} x {extents.Height:G6} {extents.Unit}, scale {extents.Scale:G6})");
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
