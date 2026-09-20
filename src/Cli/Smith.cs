using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using RfCore;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf smith &lt;path.csmith&gt;</c> — the Smith Chart tool's answer, with no display
/// (brief-smith-10-cli-verb.md; docs/design/smith-chart.md §9, P3).
///
/// <para><b>This file holds no arithmetic and it must never start.</b> Every number it prints comes
/// out of <c>src/Design/Smith/</c> — <see cref="SmithCascade"/>, <see cref="SmithReadings"/>,
/// <see cref="SmithBand"/>, <see cref="SmithDesign.Refusal"/> — which is the same code the window's
/// status strip reads, and every pixel it writes comes out of <see cref="SmithPlotBuilder"/> and
/// <see cref="SmithChartChrome"/> in <c>CircuitRF.Render</c>, which is what the window draws each
/// frame with. What is here is argument parsing, refusals and reporting, on
/// <c>src/Cli/Authoring.cs</c>' terms: <i>a rule living only in the verb is a rule the application
/// does not enforce</i>, so a document would evaluate one way headlessly and another when someone
/// opened it (R-smith10-1).</para>
///
/// <para><b>Why it depends on brief 2 and not on the window</b> (§9): the arithmetic was put below
/// the firewall on day one precisely so this phase would be wiring. The one thing it did have to
/// move is the PLOT half — the scene, the traces and the transient chrome — because a second
/// drawing path here would produce a picture that is plausible, which is indistinguishable from one
/// that is right (R-smith10-3).</para>
///
/// <para><b>With no <c>-o</c> it reports</b>, and the per-node table is the part that makes the verb
/// useful to a build machine rather than merely possible: it is the walk, one impedance per element,
/// which is what a caller comparing two revisions of a matching network actually diffs.</para>
/// </summary>
internal static class Smith
{
    // ── the argument surface (R-smith10-2) ───────────────────────────────────

    private sealed class Options
    {
        public string? Path;
        public string? Output;

        /// <summary>Evaluate here instead of the document's design frequency. As TYPED — the parse
        /// is <see cref="MatchValueFormat.TryParseWithUnit"/>'s, which is the one the window's own
        /// frequency field uses, so "2 GHz" means here what it means there.</summary>
        public string? At;

        /// <summary>Repeatable, and refused — see <see cref="CliDiagnostics.SmithSetNotApplicable"/>.</summary>
        public readonly List<string> Sets = [];

        // The picture options, in `plot`'s own spellings and through `plot`'s own refusals, so a
        // caller who has drawn one picture with this program can draw the other.
        public int?    Width, Height;
        public double  Scale = 1.0;
        public string? ScaleOption;
        public bool?   Transparent;
        public bool    Dark;
    }

    /// <summary>
    /// The chart's box, in the canvas's logical units.
    /// </summary>
    /// <remarks>
    /// <b>The window's own</b> — <c>SmithPlotBuilder.NominalCanvas</c> is the square the adaptive
    /// trajectory sampler measures its chord error in, and it is <c>DataDisplayViewModel</c>'s
    /// default square plot, which is the box <c>SmithChartViewModel</c> builds its container at. Not
    /// a page size: <see cref="PlotComposer"/> normalizes by the bounding box of what it is handed,
    /// so what this decides is the drawn ASPECT and how smooth the curves are at it.
    /// </remarks>
    private const double ChartBox = SmithPlotBuilder.NominalCanvas;

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.SmithPathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;

        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.SmithPathNotFound(o.Path));

        // The extension picks the format, and one this verb does not write is a refusal rather than
        // a file with the wrong bytes in it — `rail`'s rule, checked BEFORE anything is read,
        // because a caller that misspelled its output is going to have to type it again.
        if (o.Output is not null && OutputKindOf(o.Output) is null)
            return JsonRun.Fail(CliDiagnostics.SmithUnknownOutputFormat(
                o.Output, System.IO.Path.GetExtension(o.Output) is { Length: > 0 } e ? e : "(none)"));

        // A load is a ONE-port, and the check belongs beside the format one for its reason: both are
        // answerable from the argument alone, and a refusal that arrives after a whole report has
        // scrolled past reads as a failure of the run rather than of the flag.
        if (o.Output is not null && OutputKindOf(o.Output) == OutputKind.Touchstone
            && TouchstoneIO.ParsePortsFromExtension(o.Output) is { } ports && ports != 1)
            return JsonRun.Fail(CliDiagnostics.SmithTouchstoneNotOnePort(o.Output, ports));

        if (o.ScaleOption is not null && OutputKindOf(o.Output ?? "") != OutputKind.Png)
            return JsonRun.Fail(CliDiagnostics.PlotScaleOnVector(
                o.ScaleOption, o.Output is null ? "(no picture)" : FormatName(o.Output)));

        try { return Go(o); }
        catch (OperationCanceledException)
        {
            // R-smith10-4, and it is `render`'s own rule: nothing is written until the bytes are
            // complete, so a cancelled run leaves no file rather than half of one.
            JsonRun.Report(CliDiagnostics.SmithCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf smith <path.csmith> [--at <freq>]\n" +
            "                       [-o out.{s1p,svg,pdf,png}] [--size WxH] [--scale N | --dpi N]\n" +
            "                       [--background opaque|transparent] [--dark]");
        return 1;
    }

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; continue;
                case "--at" when i + 1 < args.Length:             o.At     = args[++i]; continue;
                case "--dark":                                    o.Dark   = true;      continue;

                case "--set" when i + 1 < args.Length:
                {
                    string text = args[++i];
                    int eq = text.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed("smith", text));
                    o.Sets.Add(text[..eq].Trim());
                    continue;
                }

                // The three picture options are `plot`'s, spelling for spelling and refusal for
                // refusal — two verbs of this program that size a page differently would be two
                // things to learn for no reason.
                case "--size" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                        || w < 8 || h < 8 || w > 20000 || h > 20000)
                        return JsonRun.Fail(CliDiagnostics.PlotSizeMalformed(args[i]));
                    o.Width = w; o.Height = h;
                    continue;
                }

                case "--scale" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !(s > 0) || s > 16)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--scale", args[i]));
                    o.Scale = s; o.ScaleOption = "--scale";
                    continue;
                }

                case "--dpi" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        || !(d > 0) || d > 1536)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--dpi", args[i]));
                    o.Scale = d / 96.0; o.ScaleOption = "--dpi";
                    continue;
                }

                case "--background" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "opaque":      o.Transparent = false; break;
                        case "transparent": o.Transparent = true;  break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownBackground(args[i]));
                    }
                    continue;

                default:
                    if (a.StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("smith", a));
                    if (o.Path is not null)
                        return JsonRun.Fail(CliDiagnostics.RunMultipleInputs("smith", a));
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    // ── what this verb writes ────────────────────────────────────────────────

    private enum OutputKind { Touchstone, Svg, Pdf, Png }

    /// <summary>The format <c>-o</c> names, or null for an extension this verb does not write.
    /// <b>No default</b> — a picture written because an extension was not recognised is the defect
    /// <c>rail</c>'s own <c>OutputKindOf</c> exists to avoid.</summary>
    private static OutputKind? OutputKindOf(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => OutputKind.Svg,
            ".pdf" => OutputKind.Pdf,
            ".png" => OutputKind.Png,
            _ => TouchstoneIO.ParsePortsFromExtension(path) is > 0 ? OutputKind.Touchstone : null,
        };

    private static string FormatName(string path) => OutputKindOf(path) switch
    {
        OutputKind.Svg => "svg",
        OutputKind.Pdf => "pdf",
        OutputKind.Png => "png",
        _              => "touchstone",
    };

    // ── the run ──────────────────────────────────────────────────────────────

    private static int Go(Options o)
    {
        RunHost.Control?.BeginStage("resolve");
        Console.Error.WriteLine("[circuitRF] resolve...");

        // ── step 1: which document ───────────────────────────────────────────
        //
        //  Inferred through DocumentKinds.Classify, exactly as `check`, `render` and `rail` infer
        //  one, and any other kind is a refusal BY KIND naming what was found — a `.csch` is refused
        //  as a schematic, not as an unreadable file, because the caller very likely meant a run verb.
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);
        if (kind != DocumentKind.Smith)
            return JsonRun.Fail(CliDiagnostics.SmithNotASmithDocument(path, DocumentKinds.Name(kind)));

        SmithDesign design;
        try { design = SmithDesignIo.LoadFromFile(path); }
        catch (Exception ex)
        { return JsonRun.Fail(CliDiagnostics.SmithDocumentUnreadable(path, ex.Message)); }

        // WHERE THIS DOCUMENT'S OVERLAY DATA IS (R-smith12-3). An overlay is a REFERENCE, by a path
        // relative to the document, and this is the same IPlotDataSources the window resolves one
        // through — so the verb and the window read one `.csmith`'s reference material through one
        // loader rather than two that could disagree about which file a reference names.
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var sources = new SmithDocumentSources(dir, inner: null, alsoSearch: ResultsDirs(dir));

        // BEFORE THE COPY BELOW, which writes the document out and reads it back: the brief-8
        // overlay block is READ and never written (R-smith12-4c), so a round trip taken before the
        // migration would drop every overlay an older `.csmith` carries, silently.
        SmithOverlayMigration.Apply(design, sources);

        // ── step 2: the overrides, on a COPY ─────────────────────────────────
        //
        //  `rail`'s rule and RailDcRun's reason: the document is the user's, and a run that wrote
        //  into it would make a second run of the same file start from a number the first supplied.
        //  Here that matters twice over, because `--at` moves the DESIGN FREQUENCY, which is a stored
        //  field of the chart and not a transient of the run.
        design = SmithDesignIo.DeserializeUnvalidated(SmithDesignIo.SerializeUnvalidated(design));

        // A `.csmith` states every quantity as a number and holds no expression scope, so there is
        // nothing for --set to land on. Refused rather than dropped — `cli.md` §3.3's
        // accepted-and-dropped defect is a run that answered a different question than the one asked.
        if (o.Sets.Count > 0)
            return JsonRun.Fail(CliDiagnostics.SmithSetNotApplicable(o.Sets[0]));

        if (o.At is { } atText)
        {
            if (!MatchValueFormat.TryParseWithUnit(atText, MatchQuantity.Frequency, "Hz",
                                                   out double atHz, out _)
                || !(atHz > 0))
                return JsonRun.Fail(CliDiagnostics.SmithAtMalformed(atText));

            design.DesignFrequencyOverrideHz = atHz;
        }

        // THERE IS NO `--sweep` ANY MORE (owner instruction, 2026-09-19). It turned the document's
        // band ON; the band is always drawn now, across the generator table's own span, so the flag
        // had nothing left to turn on. A document with a single-row table still has no band, because
        // one row is one impedance and a band needs two ends.

        // ── step 3: is the document sound ────────────────────────────────────
        //
        //  R-smith10-4: every refusal the model produces is surfaced VERBATIM. This is where --at
        //  outside the generator table's span is caught (R-smith2-5) — by the document's own rule,
        //  with the span in the sentence, and with the one-row table's exception already in it.
        if (design.Refusal() is { } refusal)
            return JsonRun.Fail(CliDiagnostics.SmithDocumentRefused(path, refusal));

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("evaluate");

        // ── step 4: the walk ─────────────────────────────────────────────────

        SmithNode[]  nodes;
        SmithReading reading;
        try
        {
            nodes   = SmithCascade.Evaluate(design, design.DesignFrequencyHz, dir);
            reading = SmithReadings.Of(design.Chart.Z0Ohm, design.DesignFrequencyHz,
                                       nodes[0].Z, nodes[^1].Z);
        }
        catch (Exception ex)
        {
            // The cascade's own sentence — a file that does not resolve, a port count that is not
            // the element's, a line quoting its length at a non-positive reference frequency.
            return JsonRun.Fail(CliDiagnostics.SmithCascadeRefused(path, ex.Message));
        }

        var band = SmithBand.Evaluate(design, dir);

        // ── step 5: report, then write ───────────────────────────────────────

        Report(design, nodes, reading, band, o);
        JsonRun.Smith = SmithJson(path, design, nodes, reading, band);

        if (o.Output is null) return 0;

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("export");

        return OutputKindOf(o.Output) == OutputKind.Touchstone
            ? WriteTouchstone(o, design, reading, band)
            : DrawChart(o, path, design, dir, sources);
    }

    /// <summary>
    /// The workspace's <c>results/</c>, when this document is inside one — the second place a
    /// relative overlay reference is looked for.
    /// </summary>
    /// <remarks>
    /// <b>The same two directories <c>CddSources</c> searches for a `.cdd`'s references</b>, because
    /// a cube overlay names a run exactly as a `.cdd` trace does: a bare <c>&lt;name&gt;.npy</c>
    /// against the flat, shared results directory. A `.csmith` outside a workspace gets the document
    /// folder alone, which is all a Touchstone reference needs.
    /// </remarks>
    private static IReadOnlyList<string> ResultsDirs(string dir)
    {
        if (CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(dir) is not { } cws)
            return [];
        if (System.IO.Path.GetDirectoryName(cws) is not { Length: > 0 } wsDir) return [];
        return [CircuitRF.Design.Results.ResultsWriter.ResultsDirectory(wsDir)];
    }

    // ── the report (R-smith10-2) ─────────────────────────────────────────────

    /// <summary>
    /// What the verb says with no <c>-o</c>: the reading the window's status strip states, and the
    /// walk one node at a time.
    /// </summary>
    /// <remarks>
    /// <b>The per-node table is the reason to run this on a build machine.</b> The reading alone
    /// answers "is it matched"; the walk answers "where did it stop being matched", which is the
    /// question a caller has when the answer is no — and it is the one thing a window can show and a
    /// pass/fail exit code cannot.
    /// </remarks>
    private static void Report(SmithDesign design, SmithNode[] nodes, SmithReading reading,
                               SmithBandResult band, Options o)
    {
        Console.WriteLine($"{design.Name} — Smith chart, Z0 {Sig(design.Chart.Z0Ohm, 4)} Ω");
        Console.WriteLine($"  design frequency   {Freq(reading.FrequencyHz)}"
                        + (o.At is not null ? "   (--at)" : ""));
        Console.WriteLine($"  generator          {Impedance(reading.GeneratorZ)}");
        Console.WriteLine($"  load               {Impedance(reading.LoadZ)}");
        Console.WriteLine($"  Γ                  {Gamma(reading.Gamma)}");
        Console.WriteLine($"  VSWR               {Finite(reading.Vswr)}");
        // Decibels and not Significant — see MatchValueFormat.Decibels; the strip spells it the same
        // way, which is the whole reason that helper is below the firewall rather than here.
        Console.WriteLine($"  mismatch           {MatchValueFormat.Decibels(reading.MismatchDb)} dB");

        if (band.Gamma.Count > 0)
            Console.WriteLine($"  swept band         {Freq(band.StartHz)} to {Freq(band.StopHz)}, "
                            + $"{band.Gamma.Count} point(s)");

        Console.WriteLine();
        Console.WriteLine("  node  element                          Z (Ω)                    Γ");

        for (int k = 0; k < nodes.Length; k++)
        {
            string name = nodes[k].ElementIndex < 0
                ? "(generator)"
                : ElementLabel(design.Elements[nodes[k].ElementIndex], nodes[k].ElementIndex);

            var g = SmithCascade.Gamma(nodes[k].Z, design.Chart.Z0Ohm);
            Console.WriteLine($"  {k,4}  {Truncate(name, 32),-32}  {Impedance(nodes[k].Z),-22}  {Gamma(g)}");
        }
    }

    /// <summary>The element's own name, or its kind and placement while it has none — the label
    /// <see cref="SmithPlotBuilder"/> puts on the same curve, so the table and the picture name the
    /// same thing.</summary>
    private static string ElementLabel(SmithElement e, int index)
        => string.IsNullOrWhiteSpace(e.Name) ? $"{e.Placement} {e.Kind} [{index}]" : e.Name;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ── the formatting, all of it MatchValueFormat's ─────────────────────────
    //
    //  The strip's own spellings (R-smith4-8), which is the whole reason that file came below the
    //  firewall: a terminal and a window reporting one document must not disagree about how a
    //  number reads.

    private static string Freq(double hz)
        => MatchValueFormat.FormatWithUnit(hz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 5);

    private static string Sig(double v, int digits) => MatchValueFormat.Significant(v, digits);

    private static string Finite(double v)
        => double.IsFinite(v) ? MatchValueFormat.Significant(v, 4) : MatchValueFormat.InfinityGlyph;

    private static string Impedance(Complex z)
        => $"{Sig(z.Real, 4)} {(z.Imaginary < 0 ? "−" : "+")} j{Sig(Math.Abs(z.Imaginary), 4)}";

    private static string Gamma(Complex g)
        => $"{Sig(g.Magnitude, 4)} ∠{Sig(g.Phase * 180.0 / Math.PI, 3)}°";

    // ── -o out.s1p ───────────────────────────────────────────────────────────

    /// <summary>
    /// The load Γ as Touchstone.
    /// </summary>
    /// <remarks>
    /// <b>One point, or the band's.</b> With no swept band the file holds the one frequency the
    /// report is about; with the band on (the document's own, or <c>--sweep</c>'s) it holds exactly
    /// the locus the picture draws. That is the only pair of answers that cannot surprise anyone: a
    /// caller who asked for a band and got one point, or who asked for a point and got a band,
    /// would have a file that plots as something they did not run.
    ///
    /// <para><b>A LOAD IS A ONE-PORT</b>, so anything other than <c>.s1p</c> is refused rather than
    /// padded — a `.s2p` of a reflection coefficient would be three quarters invented.</para>
    ///
    /// <para><b>No date comment</b>, deliberately: the same document written twice produces the same
    /// bytes, which is what lets the gate compare a process against an in-process call with no
    /// exclusion at all.</para>
    /// </remarks>
    private static int WriteTouchstone(Options o, SmithDesign design, SmithReading reading,
                                       SmithBandResult band)
    {
        // The port count was settled in Run, before anything was read — see there.
        //
        // THE GRID IS THE BAND'S OWN. SmithBandResult carries the frequency it took each sample at,
        // so the file's rows are the points that were actually walked rather than a second copy of
        // the spacing rule reconstructed from the two ends — which would agree with SmithBand until
        // one of them was changed, and then differ silently in a file nobody would re-check.
        bool wholeBand = band.Gamma.Count > 1;

        double[]  freqs = wholeBand ? [.. band.FrequencyHz] : [reading.FrequencyHz];
        Complex[] gamma = wholeBand ? [.. band.Gamma]       : [reading.Gamma];

        var snp = new SNP(freqs, 1, MatrixType.S, MatrixFormat.MA,
                          new Complex(design.Chart.Z0Ohm, 0.0));
        for (int i = 0; i < freqs.Length; i++) snp.Matrices[i][0, 0] = gamma[i];

        // ASCII ONLY in these two lines: TouchstoneIO writes the file in Encoding.ASCII, so an em
        // dash or an ohm sign lands as '?' — visible, harmless and shabby, and a reader would take it
        // for a corrupted file rather than for a comment nobody checked.
        snp.Comments.Add(new CommentEntry(-1,
            $"circuitRF smith - the load reflection coefficient of '{design.Name}'"));
        snp.Comments.Add(new CommentEntry(-1,
            $"Reference impedance {Sig(design.Chart.Z0Ohm, 6)} Ohm, the chart's own"));

        try
        {
            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.Output!));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            TouchstoneIO.WriteFile(snp, o.Output!);
        }
        catch (Exception ex)
        { return JsonRun.Fail(CliDiagnostics.SmithWriteFailed(o.Output!, ex.Message)); }

        JsonRun.AddOutput("touchstone", o.Output!);
        Console.WriteLine();
        Console.WriteLine($"Wrote {o.Output} ({freqs.Length} point(s), 1-port S)");
        return 0;
    }

    // ── -o out.{svg,pdf,png} (R-smith10-3) ───────────────────────────────────

    /// <summary>
    /// The chart, through the one composer.
    /// </summary>
    /// <remarks>
    /// <b>No second renderer.</b> The scene and the traces are <see cref="SmithPlotBuilder"/>'s —
    /// the constant-Q arcs, the trajectories, the swept band, the load points, the conjugate targets,
    /// the overlays and the document's markers, in the window's own draw order — the transient chrome
    /// is <see cref="SmithChartChrome"/>'s, and the page, the theme and the encoder are
    /// <see cref="RenderDataDisplay.Emit"/>'s, which is what <c>render --data</c> and <c>plot</c>
    /// already compose through.
    ///
    /// <para><b>Nothing is narrowed in place.</b> There is no per-render layer or overlay selection
    /// here at all — which is the cheapest possible answer to <c>TechnologyCache</c>'s defect, where
    /// a shared instance narrowed once stays narrowed for every later call in the same process.</para>
    ///
    /// <para><b>The chrome is drawn with nothing hovered and nothing dragged</b>
    /// (<see cref="SmithChromeState.None"/>): those are states a live pointer has and an export does
    /// not, and inventing one would put a highlighted ring on a picture nobody was touching.</para>
    /// </remarks>
    private static int DrawChart(Options o, string path, SmithDesign design, string dir,
                                 SmithDocumentSources sources)
    {
        var scene = SmithPlotBuilder.BuildScene(design, dir, (ChartBox, ChartBox), window: null);

        // The overlays a `.csmith` names, resolved exactly as the window resolves them — through
        // `PlotConfigLoader.LoadTrace`, which is the `.cdd`'s own reader and the only one
        // (R-smith12-5). One that does not resolve is REPORTED and the chart carries on drawing
        // everything that did (R-smith8-2): reference material that is missing must not take the
        // work down with it.
        var overlays = new List<SmithOverlayTrace>();
        foreach (var stored in design.Overlays)
        {
            var cfg = SmithOverlays.Read(stored);
            if (cfg is not null && SmithOverlays.Load(cfg, sources) is { } trace)
            {
                overlays.Add(new SmithOverlayTrace(SmithOverlays.Key(cfg), trace));
                continue;
            }

            string label = cfg is not null ? SmithOverlays.Key(cfg) : "overlay";
            string why   = cfg is null
                ? "this overlay is not a trace circuitRF can read."
                : sources.WhyUnresolved(cfg.SourcePath)
                  ?? $"'{cfg.SourcePath}' is not one of the data sets that are open.";

            var note = CliDiagnostics.SmithOverlayUnresolved(label, why);
            Console.Error.WriteLine("warning: " + note.Render());
            JsonRun.Note(note);
        }

        // The plot is the BUILDER's — what a Smith Chart plot is, is said in one place, and a second
        // list of its settings here is the copy that would drift (R-smith10-1).
        var plot = SmithPlotBuilder.NewChartPlot();
        SmithPlotBuilder.Fill(plot, scene, design, autoscale: true, overlays);

        var placed = RenderDataDisplay.Place(
            plot, left: 0, top: 0, width: ChartBox, height: ChartBox, FreqUnit.GHz,
            hasMultipleSources: false, aliasFor: null,
            overlay: (canvas, tf, theme) =>
                SmithChartChrome.Draw(canvas, tf, theme, scene, design, SmithChromeState.None));

        var req = new RenderDataDisplay.Request(
            o.Output!, FormatName(o.Output!), Data: [], Tab: null, PlotIndex: null, AllTabs: false,
            PageWidth: o.Width, PageHeight: o.Height, Scale: o.Scale,
            Transparent: o.Transparent, Dark: o.Dark);

        return RenderDataDisplay.Emit(
            path, DocumentKinds.Name(DocumentKind.Smith), req, [[placed]], display: null,
            $"  {plot.Traces.Count} trace(s), {scene.Nodes.Count} node(s)");
    }

    // ── --json (R-aut1-6) ────────────────────────────────────────────────────

    /// <summary>
    /// The structured answer: the reading, the walk and the band.
    /// </summary>
    /// <remarks>
    /// <b>The same numbers the table prints, not a projection of them.</b> A caller parsing this is
    /// asking the question the terminal answers, and a field the human form has and this one does
    /// not is a field somebody will go back to scraping stdout for.
    /// </remarks>
    private static SmithReportJson SmithJson(
        string path, SmithDesign design, SmithNode[] nodes, SmithReading reading,
        SmithBandResult band)
    {
        var walk = new List<SmithNodeJson>(nodes.Length);
        for (int k = 0; k < nodes.Length; k++)
        {
            var g = SmithCascade.Gamma(nodes[k].Z, design.Chart.Z0Ohm);
            walk.Add(new SmithNodeJson(
                k,
                nodes[k].ElementIndex < 0 ? null : ElementLabel(design.Elements[nodes[k].ElementIndex],
                                                                nodes[k].ElementIndex),
                nodes[k].ElementIndex < 0 ? null : design.Elements[nodes[k].ElementIndex].Kind.ToString(),
                nodes[k].ElementIndex < 0 ? null : design.Elements[nodes[k].ElementIndex].Placement.ToString(),
                [nodes[k].Z.Real, nodes[k].Z.Imaginary],
                [g.Real, g.Imaginary]));
        }

        return new SmithReportJson(
            System.IO.Path.GetFullPath(path),
            design.Name,
            design.Chart.Z0Ohm,
            reading.FrequencyHz,
            [reading.GeneratorZ.Real, reading.GeneratorZ.Imaginary],
            [reading.LoadZ.Real, reading.LoadZ.Imaginary],
            [reading.Gamma.Real, reading.Gamma.Imaginary],
            double.IsFinite(reading.Vswr) ? reading.Vswr : null,
            double.IsFinite(reading.MismatchDb) ? reading.MismatchDb : null,
            walk,
            band.Gamma.Count > 0
                ? new SmithBandJson(band.StartHz, band.StopHz, band.Gamma.Count)
                : null);
    }
}
