using System.Globalization;
using System.Text;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf rail &lt;path.crail&gt;</c> — the whole railRF window, with no display
/// (brief-railrf-10-cli-verb.md; railrf.md §11.5, §5).
///
/// <para><b>This file holds no analysis logic and it must never start.</b> Every number it prints
/// comes out of <c>src/Design/RailRf/</c> — <see cref="RailDcRun"/>, <see cref="RailOrder"/>,
/// <c>PdnGraphExtractor</c>/<c>PdnMeshExtractor</c>, <c>PdnViaCheck</c> — which is the same code the
/// window's Run button drives, and every pixel of an SVG or PDF report comes out of
/// <see cref="RailReportPage"/> in <c>CircuitRF.Render</c>. What is here is argument parsing,
/// refusals and reporting, on <c>src/Cli/Authoring.cs</c>' terms: <i>an operation that lives only in
/// a view model is not a capability, and a verb that re-implements one diverges from it
/// silently.</i></para>
///
/// <para><b>Why the verb exists at all</b> (§5): a board that can only be judged by opening a window
/// cannot be judged in CI. This is the one command that reads every companion file, runs both
/// extractors and the whole solve, and answers with an exit code — which also makes it the cheapest
/// way to exercise a real reference board (R-rail10-9).</para>
///
/// <para><b>Omitting <c>--rail</c> runs them ALL</b>, in <see cref="RailOrder"/>'s dependency order —
/// the shape <c>hb</c>/<c>lp</c> already have for a wrapped sweep, and for the same reason: running
/// one rail of a chain alone silently drops the upstream answer the downstream rail's source starts
/// from.</para>
///
/// <para><b>An unstated value is a refusal and an unstated CURRENT is not</b> (R-rail10-3). The
/// distinction is the whole of Q-16: a load with no current is an OBSERVATION port, which is what it
/// means in the window, and the report lists it as observed rather than omitting it or defaulting it
/// to zero.</para>
/// </summary>
internal static class Rail
{
    // ── the argument surface (R-rail10-1) ────────────────────────────────────

    private sealed class Options
    {
        public string? Path;
        public string? Output;
        public string? RailName;
        public PdnModelKind Model = PdnModelKind.Fast;

        /// <summary>Repeatable. <c>REFDES.PIN=&lt;model&gt;</c> — see <see cref="ApplySource"/>.</summary>
        public readonly List<string> Sources = [];

        /// <summary>Repeatable. <c>REFDES.PIN=&lt;current&gt;</c>, or bare for an observation port.</summary>
        public readonly List<string> Loads = [];

        public string? TargetDrop;
        public string? TargetZ;

        /// <summary>Repeatable. <c>&lt;file&gt;</c> or <c>PORT=&lt;file&gt;</c>.</summary>
        public readonly List<string> Masks = [];

        /// <summary>Repeatable. <c>name=freq[xN]</c>.</summary>
        public readonly List<string> Aggressors = [];

        public string? Reference;
        public string? Extent;

        /// <summary>Repeatable. <c>name=expr</c>, exactly as every run verb spells it.</summary>
        public readonly List<(string Name, string Expr)> Sets = [];

        public int  Rows = 10;
        public bool All;
    }

    /// <summary>The report page's size, in points for a vector format. <c>render</c>'s own default,
    /// so the two verbs' pictures are the same size unless someone asks otherwise.</summary>
    private const int PageW = 1600;
    private const int PageH = 1200;

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null) { JsonRun.Report(CliDiagnostics.RailPathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;

        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.RailPathNotFound(o.Path));

        // R-rail10-4: the extension picks the format, and one this verb does not write is a refusal
        // rather than a file with the wrong bytes in it. Checked BEFORE anything is read or solved,
        // because a caller that misspelled its output is going to have to type it again.
        if (o.Output is not null && OutputKindOf(o.Output) is null)
            return JsonRun.Fail(CliDiagnostics.RailUnknownOutputFormat(
                o.Output, Path.GetExtension(o.Output) is { Length: > 0 } e ? e : "(none)"));

        try { return Go(o); }
        catch (OperationCanceledException)
        {
            // R-rail10-6, and it is `render`'s own rule: a partial `.csv` of a half-solved board is
            // worse than no file, so nothing is opened until the answer is complete.
            JsonRun.Report(CliDiagnostics.RailCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf rail <path.crail | path.clay | path.csch | cell-folder>\n" +
            "                      [--rail NAME] [--fast | --accurate]\n" +
            "                      [--source REFDES.PIN=<v[,r][,l] | file.sNp>]...\n" +
            "                      [--load REFDES.PIN[=<current>]]...\n" +
            "                      [--target-drop <mV>] [--target-z <mOhm>] [--mask [PORT=]<file>]\n" +
            "                      [--aggressor NAME=<freq>[xN]]...\n" +
            "                      [--reference <layer>] [--extent as-imported|filled|infinite]\n" +
            "                      [--set var=expr] [--rows N|--all] [-o out.{csv,npy,mat,txt,svg,pdf}]");
        return 1;
    }

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length:      o.Output     = args[++i]; continue;
                case "--rail" when i + 1 < args.Length:                o.RailName   = args[++i]; continue;
                case "--reference" when i + 1 < args.Length:           o.Reference  = args[++i]; continue;
                case "--extent" when i + 1 < args.Length:              o.Extent     = args[++i]; continue;
                case "--target-drop" when i + 1 < args.Length:         o.TargetDrop = args[++i]; continue;
                case "--target-z" when i + 1 < args.Length:            o.TargetZ    = args[++i]; continue;
                case "--mask" when i + 1 < args.Length:      o.Masks.Add(args[++i]);      continue;
                case "--source" when i + 1 < args.Length:    o.Sources.Add(args[++i]);    continue;
                case "--load" when i + 1 < args.Length:      o.Loads.Add(args[++i]);      continue;
                case "--aggressor" when i + 1 < args.Length: o.Aggressors.Add(args[++i]); continue;

                case "--fast":     o.Model = PdnModelKind.Fast;     continue;
                case "--accurate": o.Model = PdnModelKind.Accurate; continue;
                case "--all":      o.All = true;                    continue;

                case "--rows" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows)
                        || rows < 0)
                        return JsonRun.Fail(CliDiagnostics.RailRowsMalformed(args[i]));
                    o.Rows = rows;
                    continue;

                case "--set" when i + 1 < args.Length:
                {
                    string text = args[++i];
                    int eq = text.IndexOf('=');
                    if (eq <= 0) return JsonRun.Fail(CliDiagnostics.SetMalformed("rail", text));
                    o.Sets.Add((text[..eq].Trim(), text[(eq + 1)..].Trim()));
                    continue;
                }

                default:
                    if (a.StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("rail", a));
                    if (o.Path is not null)
                        return JsonRun.Fail(CliDiagnostics.RunMultipleInputs("rail", a));
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    // ── what this verb writes (R-rail10-4) ───────────────────────────────────

    private enum OutputKind { Csv, Npy, Mat, Tsv, Svg, Pdf, Touchstone }

    /// <summary>The format <c>-o</c> names, or null for an extension this verb does not write.
    /// <b>No default</b> — writing a `.mat` because the extension was not recognised is the defect
    /// `sparam`'s own <c>SparamDataFormat</c> exists to avoid, one verb along.</summary>
    private static OutputKind? OutputKindOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv"           => OutputKind.Csv,
            ".npy"           => OutputKind.Npy,
            ".mat"           => OutputKind.Mat,
            ".txt" or ".tsv" => OutputKind.Tsv,
            ".svg"           => OutputKind.Svg,
            ".pdf"           => OutputKind.Pdf,
            _ => RfCore.TouchstoneIO.ParsePortsFromExtension(path) is > 0 ? OutputKind.Touchstone : null,
        };
    }

    // ── the five steps ───────────────────────────────────────────────────────

    private static int Go(Options o)
    {
        RunHost.Control?.BeginStage("resolve");
        Progress("resolve");

        var (input, inputRefusal) = ResolveInput(o);
        if (inputRefusal is { } ir) return ir;

        var (board, boardRefusal) = ResolveBoard(input!);
        if (boardRefusal is { } br) return br;

        // R-rail10-1's overrides land on a COPY of the document, for RailDcRun's own stated reason:
        // the document is the user's, and a run that wrote into it would make a second run of the
        // same file start from a number the first run supplied.
        var doc = RailDocumentIo.DeserializeUnvalidated(RailDocumentIo.SerializeUnvalidated(input!.Document));
        doc.Name = input.Document.Name;

        if (ApplyOverrides(o, doc, board!, input.DocumentPath) is { } overrideRefusal) return overrideRefusal;

        // Accepted-and-dropped is the defect `cli.md` §3.3 records, and it is the same one
        // CliDiagnostics.RailSetNotApplicable refuses one flag along: --target-z, --mask and
        // --aggressor are read, validated and written onto the rail, and then a DC answer has
        // nowhere to show any of them. Said out loud rather than left to a caller to notice that a
        // mask they supplied changed no number on the page.
        var frequencyFlags = new List<string>();
        if (o.TargetZ is not null)       frequencyFlags.Add("--target-z");
        if (o.Masks.Count > 0)           frequencyFlags.Add("--mask");
        if (o.Aggressors.Count > 0)      frequencyFlags.Add("--aggressor");
        if (frequencyFlags.Count > 0)
        {
            var note = CliDiagnostics.RailFrequencyFlagsNotInThisPhase(Join(frequencyFlags));
            Console.Error.WriteLine("note: " + note.Render());
            JsonRun.Note(note);
        }

        if (doc.Refusal() is { } docRefusal)
            return JsonRun.Fail(CliDiagnostics.RailDocumentRefused(input.DocumentPath, docRefusal));

        // R-rail10-3, row 1. The ENGINE refuses a rail with no reference layer too, and its sentence
        // is the right one for a window; headless the answer has to name the flag that supplies it,
        // because a terminal has no combo box to turn red.
        var chosen = Selected(doc, o.RailName);
        if (chosen is null)
            return JsonRun.Fail(CliDiagnostics.RailNoSuchRail(
                o.RailName!, Join([.. doc.Rails.Select(r => r.Name)])));

        foreach (var rail in chosen)
            if (rail.ReferenceLayer is null)
                return JsonRun.Fail(CliDiagnostics.RailNoReferenceLayer(rail.Name));

        // R-rail10-1: with no --rail this runs them ALL, so a rail the caller did not name may not be
        // left unsolved by a document-level refusal it would have hit anyway.
        var order = RailOrder.Resolve(doc);
        if (order.Refusal is { } orderRefusal)
            return JsonRun.Fail(CliDiagnostics.RailOrderRefused(orderRefusal));

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("solve");
        Progress(o.Model == PdnModelKind.Accurate ? "solve (accurate)" : "solve (fast)");

        // The companions the document names — what makes a REFDES anchor resolve to copper and what
        // PdnMountingLoopExtractor reads a part's mounting loop from. Through RailArtwork's own
        // walks, which is what the window's open calls, so the two surfaces cannot land on
        // different files. The netlist's units are cross-checked against the ARTWORK's extent
        // (R-gi5-10), so it needs the artwork's own resolution and is read after it.
        var netlist = RailArtwork.ResolveBoardNetlist(
            doc, input.DocumentPath, board!.View.DbuPerMicron,
            out string? netlistPath, out string? netlistError);
        if (netlistError is { Length: > 0 })
        {
            Console.Error.WriteLine(
                $"warning: the board netlist '{netlistPath}' did not read: {netlistError}");
            JsonRun.Note(CliDiagnostics.RailBoardNetlistUnreadable(netlistPath!, netlistError));
        }
        foreach (string d in netlist?.Diagnostics ?? [])
        {
            Console.Error.WriteLine("note: " + d);
            JsonRun.Note(CliDiagnostics.RailRunNote(d));
        }

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document       = doc,
            Shapes         = board.View.Shapes,
            Technology     = board.Technology,
            DbuPerMicron   = board.View.DbuPerMicron,
            LengthFormat   = board.LengthFormat,
            Model          = o.Model,
            Pads           = PdnBoardPads.PadsOf(netlist),
            NetPoints      = PdnBoardPads.NetPointsOf(netlist),
            ReferenceNet   = doc.ReferenceNet,
        });

        foreach (string d in run.Diagnostics)
        {
            Console.Error.WriteLine("note: " + d);
            JsonRun.Note(CliDiagnostics.RailRunNote(d));
        }

        if (run.Refusal is { } why)
        {
            // R-rail10-6: a refusal stays a refusal, with the run service's own sentence — `em`'s
            // rule, and for its reason: collapsing them into "the rail was not solved" throws away
            // the only part a caller can act on.
            Console.Error.WriteLine("error: " + why);
            return JsonRun.Fail(CliDiagnostics.RailRefused(why));
        }

        RunHost.Cancellation.ThrowIfCancellationRequested();

        // Only the rails the caller asked for are reported and exported; the rest were solved because
        // the chain needed them, which the order line says.
        var wanted = chosen.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = run.Rails.Where(r => wanted.Contains(r.RailName)).ToList();

        var provenance = Provenance(doc, o, board, input.DocumentPath, chosen, results);

        Report(doc, run, results, order, provenance, o);

        // The DataSet is built whether or not anything is exported, because `--json` carries it and
        // the shape a caller reads is the same either way.
        var data = Pack(results, provenance);
        JsonRun.Data = data;
        JsonRun.Rail = RailJson(doc, run, results, order, provenance, o.Model);

        if (o.Output is null) return 0;

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("export");
        Progress("export");

        return Export(o, input, board, doc, results, provenance, data);
    }

    // ── step 1: which document (R-rail10-2) ──────────────────────────────────

    /// <summary>What the input path resolved to.</summary>
    /// <param name="Document">The rail set.</param>
    /// <param name="DocumentPath">The `.crail` it was read from.</param>
    /// <param name="ClayHint">The artwork the INPUT named, where the input was a layout or a cell
    /// folder rather than a `.crail`. Null on a `.crail`, which names its own.</param>
    private sealed record Input(RailDocument Document, string DocumentPath, string? ClayHint);

    /// <summary>
    /// R-rail10-2: a `.crail`, a `.clay`, a `.csch` and a cell folder are all first-class inputs, and
    /// the kind is inferred through <see cref="DocumentKinds.Classify"/> — the same function
    /// <c>check</c>, <c>render</c> and <c>netlist</c> infer with. <b>Any other kind is a refusal BY
    /// KIND</b>, naming what was handed over: a `.cnl` is refused as a netlist, not as an unreadable
    /// file.
    /// </summary>
    private static (Input? Input, int? Refusal) ResolveInput(Options o)
    {
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);

        switch (kind)
        {
            case DocumentKind.Rail:
            {
                RailDocument doc;
                try { doc = RailDocumentIo.LoadFromFile(path); }
                catch (Exception ex)
                { return (null, JsonRun.Fail(CliDiagnostics.RailDocumentUnreadable(path, ex.Message))); }
                return (new Input(doc, Path.GetFullPath(path), null), null);
            }

            case DocumentKind.Layout:
            case DocumentKind.Schematic:
            case DocumentKind.Cell:
            {
                string dir = Directory.Exists(path)
                    ? Path.GetFullPath(path)
                    : Path.GetDirectoryName(Path.GetFullPath(path))!;

                // A view file lives in the cell's own `layout/` or `schematic/` sub-folder, and the
                // `.crail` beside a cell is in the CELL folder — so the search starts at the
                // document's directory and takes one step up, which covers both spellings.
                string? crail = FindCrail(dir) ?? FindCrail(Path.GetDirectoryName(dir));
                if (crail is null)
                    // R-rail10-2's interesting case: there is a board and no rail declaration. The
                    // verb does NOT author one — `new`'s own rule: once a document exists, the way to
                    // change it is to WRITE it, because the format is the contract.
                    return (null, JsonRun.Fail(CliDiagnostics.RailNoDocumentBeside(
                        path, DocumentKinds.Name(kind))));

                RailDocument doc;
                try { doc = RailDocumentIo.LoadFromFile(crail); }
                catch (Exception ex)
                { return (null, JsonRun.Fail(CliDiagnostics.RailDocumentUnreadable(crail, ex.Message))); }

                string? clay = kind == DocumentKind.Layout
                    ? Path.GetFullPath(path)
                    : LayoutOf(Directory.Exists(path) ? path : Path.GetDirectoryName(dir)!);

                return (new Input(doc, Path.GetFullPath(crail), clay), null);
            }

            default:
                return (null, JsonRun.Fail(CliDiagnostics.RailNotARailDocument(path, DocumentKinds.Name(kind))));
        }
    }

    private static string? FindCrail(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*" + RailDocumentIo.Extension)
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .FirstOrDefault();
    }

    /// <summary>The cell folder's primary layout view, or null. <c>CellFolder.ResolvePrimary</c>'s
    /// answer and no other, so the artwork this reads is the artwork <c>render</c> would draw.</summary>
    private static string? LayoutOf(string cellDir) => RailArtwork.LayoutOf(cellDir);

    // ── step 2: the artwork and the stackup ──────────────────────────────────

    /// <param name="View">The artwork, as the layout editor reads it.</param>
    /// <param name="Technology">The stackup.</param>
    /// <param name="ClayPath">Where the artwork came from, for the report.</param>
    /// <param name="BaseDir">What an instance's <c>CellRef</c> resolves against.</param>
    private sealed record BoardInputs(LayoutView View, Technology Technology, string ClayPath, string BaseDir)
    {
        /// <summary>The artwork's own units — what every coordinate on this run's report reads in
        /// (owner, 2026-09-18). The `.clay`'s own display unit, which is what the layout editor shows
        /// the same board in.</summary>
        public RailLengthFormat LengthFormat => RailLengthFormat.For(View);
    }

    /// <summary>
    /// Resolves the artwork and the stackup by the WALKS the application already performs — the
    /// artwork through the document-relative <c>ArtworkCellRef</c> (<c>RefPath.Resolve</c>, the rule
    /// every circuitRF reference follows) and the technology through
    /// <see cref="TechnologyResolver.ResolveForDocument"/>, which is what <c>em</c> and <c>render</c>
    /// both resolve with.
    ///
    /// <para><b>The technology walk starts from a different file than the artwork walk</b>, and that
    /// is deliberate rather than an oversight: a <c>TechnologyRef</c> on the `.crail` is relative to
    /// the `.crail`, and with none stated the layout's own reference and its own ancestor workspace
    /// are what the layout editor would use. Both walks are printed, for <c>explain</c>'s own reason
    /// — which one produced the answer is the part a caller cannot otherwise see.</para>
    /// </summary>
    private static (BoardInputs? Board, int? Refusal) ResolveBoard(Input input)
    {
        // THE WALKS ARE NOT HERE. `RailArtwork.Resolve` owns them, and the window's own open goes
        // through the same call — see that file's header. What stays here is this verb's reporting:
        // which stderr lines it prints, which diagnostic code each refusal carries, and its exit.
        var found = RailArtwork.Resolve(
            input.Document, input.DocumentPath, input.ClayHint, new TechnologyCache());

        string clay = found.ClayPath ?? "(no layout view)";

        switch (found.Outcome)
        {
            case RailArtworkOutcome.NoArtworkRef:
                return (null, JsonRun.Fail(CliDiagnostics.RailNoArtwork(input.DocumentPath)));
            case RailArtworkOutcome.NotFound:
                return (null, JsonRun.Fail(CliDiagnostics.RailArtworkNotFound(
                    input.DocumentPath, found.Detail ?? "", clay)));
            case RailArtworkOutcome.Unreadable:
                return (null, JsonRun.Fail(CliDiagnostics.RailArtworkUnreadable(
                    clay, found.Detail ?? "")));
        }

        foreach (string d in found.Diagnostics)
        {
            Console.Error.WriteLine("warning: " + d);
            JsonRun.Note(CliDiagnostics.RailTechnologyWarning(d));
        }

        Console.Error.WriteLine($"[circuitRF] document:   {input.DocumentPath}");
        Console.Error.WriteLine($"[circuitRF] artwork:    {clay}");
        Console.Error.WriteLine(found.WorkspaceCwsPath is null
            ? "[circuitRF] workspace:  none above it — references resolve against the document's own directory"
            : $"[circuitRF] workspace:  {found.WorkspaceCwsPath}");

        // R-rail10-3's shape, one row along: railRF prices copper against a stackup, so a run with no
        // technology is not a degraded picture the way an orphan `.clay` is for `render` — it is an
        // answer with no thicknesses and no conductivities in it. Refused, naming what supplies one.
        if (found is not { Technology: { } tech, View: { } view })
            return (null, JsonRun.Fail(CliDiagnostics.RailNoTechnology(clay)));

        Console.Error.WriteLine($"[circuitRF] technology: {found.TechnologyPath} ({found.TechnologySource})");

        return (new BoardInputs(view, tech, clay, CellHierarchy.BaseDirOfDocument(clay)), null);
    }

    // ── step 3: the overrides (R-rail10-1) ───────────────────────────────────

    /// <summary>
    /// Which rails this run reports, or null when <c>--rail</c> named one the document does not hold.
    /// </summary>
    private static List<RailSpec>? Selected(RailDocument doc, string? name)
    {
        if (name is null) return [.. doc.Rails];
        return doc.Rail(name) is { } one ? [one] : null;
    }

    private static int? ApplyOverrides(Options o, RailDocument doc, BoardInputs board, string documentPath)
    {
        // A `.crail` declares no variables of its own — every number in it is a stated quantity in
        // base SI — so there is nothing for `--set` to REPLACE. Reported as a refusal naming the
        // flags that do state these quantities, rather than accepted and dropped: a run that
        // answered a different question than the one asked is exactly the defect §3.3 records for
        // the five older run verbs, and it costs the same here.
        if (o.Sets.Count > 0)
            return JsonRun.Fail(CliDiagnostics.RailSetNotApplicable(o.Sets[0].Name));

        var targets = Selected(doc, o.RailName);
        if (targets is null)
            return JsonRun.Fail(CliDiagnostics.RailNoSuchRail(
                o.RailName!, Join([.. doc.Rails.Select(r => r.Name)])));

        if (targets.Count == 0 &&
            (o.Sources.Count > 0 || o.Loads.Count > 0 || o.Reference is not null || o.Extent is not null
             || o.TargetDrop is not null || o.TargetZ is not null || o.Masks.Count > 0
             || o.Aggressors.Count > 0))
            return JsonRun.Fail(CliDiagnostics.RailNoRails(doc.Name));

        if (o.Reference is { } referenceName)
        {
            if (LayerOf(board.Technology, board.View, referenceName) is not { } key)
                return JsonRun.Fail(CliDiagnostics.RailUnknownLayer(
                    referenceName, Join(LayerNames(board.Technology))));
            foreach (var rail in targets) rail.ReferenceLayer = key;
        }

        if (o.Extent is { } extentText)
        {
            if (ExtentOf(extentText) is not { } extent)
                return JsonRun.Fail(CliDiagnostics.RailUnknownExtent(extentText));
            foreach (var rail in targets) rail.ReferenceExtent = extent;
        }

        if (o.TargetDrop is { } dropText)
        {
            if (!TryValue(dropText, out double volts))
                return JsonRun.Fail(CliDiagnostics.RailValueMalformed("--target-drop", dropText, "80mV"));
            foreach (var rail in targets) rail.DropBudget = RailTarget.OfDropBudget(volts * 1e3);
        }

        if (o.TargetZ is { } zText)
        {
            if (!TryValue(zText, out double ohms))
                return JsonRun.Fail(CliDiagnostics.RailValueMalformed("--target-z", zText, "2.5mOhm"));
            foreach (var rail in targets) rail.ImpedanceTarget = RailTarget.OfFlatImpedance(ohms * 1e3);
        }

        foreach (string spec in o.Sources)
            // Relative to the `.crail`, not to the artwork — RailSource.TouchstoneRef's own rule, and
            // the portability rule every other circuitRF document reference follows.
            if (ApplySource(spec, targets, Path.GetDirectoryName(documentPath)!) is { } r) return r;

        foreach (string spec in o.Loads)
            if (ApplyLoad(spec, targets) is { } r) return r;

        foreach (string spec in o.Masks)
            if (ApplyMask(spec, targets, board.LengthFormat) is { } r) return r;

        foreach (string spec in o.Aggressors)
            if (ApplyAggressor(spec, targets) is { } r) return r;

        return null;
    }

    /// <summary>
    /// <c>--source BT1.1=3.7V,50mOhm</c> or <c>--source BT1.1=cell.s1p</c>.
    /// </summary>
    /// <remarks>
    /// <b>A row for the same anchor is REPLACED, not added beside.</b> Two sources on one rail are
    /// two branches in the same mesh (§2.2), so appending a second row at the same pad would change
    /// the circuit rather than the value — and a flag whose effect depends on whether the document
    /// already mentioned that pad is a flag nobody can reason about.
    /// </remarks>
    private static int? ApplySource(string spec, List<RailSpec> rails, string docDir)
    {
        var (anchorText, modelText, split) = Split(spec);
        if (!split || modelText.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RailSourceMalformed(spec));

        if (AnchorOf(anchorText) is not { } anchor)
            return JsonRun.Fail(CliDiagnostics.RailAnchorMalformed("--source", anchorText));

        double? v = null, r = null, l = null;
        string? file = null;

        foreach (string field in modelText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string text = field;
            string? key = null;
            int eq = text.IndexOf('=');
            if (eq > 0) { key = text[..eq].Trim().ToLowerInvariant(); text = text[(eq + 1)..].Trim(); }

            // A Touchstone file is named by its extension, exactly as it is everywhere else on this
            // surface — and where one is given it IS the model, which RailSource then refuses to see
            // beside an R-L rather than silently preferring one of the two.
            if (key == "file" || RfCore.TouchstoneIO.ParsePortsFromExtension(text) is > 0)
            { file = System.IO.Path.IsPathRooted(text) ? text : CircuitRF.Core.RefPath.Resolve(docDir, text); continue; }

            if (!TryValue(text, out double value))
                return JsonRun.Fail(CliDiagnostics.RailSourceFieldMalformed(field, spec));

            switch (key)
            {
                case "v": v = value; continue;
                case "r": r = value; continue;
                case "l": l = value; continue;
                case null:
                    // Untagged, so the UNIT says which quantity it is — the same rule a `.cnl`'s
                    // inline unit follows. An untagged bare number cannot be told apart from three
                    // quantities and is refused rather than guessed at.
                    string unit = UnitNormalizer.ToEngineUnit(UnitPartOf(text));
                    if (unit.EndsWith("Ohm", StringComparison.Ordinal) ||
                        unit.EndsWith("ohm", StringComparison.Ordinal) ||
                        unit.EndsWith("Ohms", StringComparison.Ordinal)) { r = value; continue; }
                    if (unit.EndsWith('H')) { l = value; continue; }
                    if (unit.EndsWith('V')) { v = value; continue; }
                    return JsonRun.Fail(CliDiagnostics.RailSourceFieldUntagged(field, spec));
                default:
                    return JsonRun.Fail(CliDiagnostics.RailSourceFieldUnknown(key, spec));
            }
        }

        var row = new RailSource
        {
            Anchor = anchor,
            OpenCircuitVoltageV = v,
            SeriesResistanceOhms = r,
            SeriesInductanceHenries = l,
            TouchstoneRef = file,
        };

        foreach (var rail in rails) Replace(rail.Sources, row, s => s.Anchor);
        return null;
    }

    /// <summary>
    /// <c>--load U1.VDD=120mA</c>, or <c>--load U1.VDD</c> for an observation port.
    /// </summary>
    /// <remarks>
    /// <b>R-rail10-3's non-refusal, and it is the half that is easy to implement backwards.</b> A
    /// load with no current is not a malformed load — it is an OBSERVATION port, which is what it
    /// means in the window (Q-16), and it contributes nothing to the DC solve while still appearing
    /// on the report. Refusing it here, or defaulting it to zero, would make <i>not added</i> and
    /// <i>added with no current</i> indistinguishable — which is the one thing
    /// <see cref="RailLoad.DcCurrentA"/>'s own nullability exists to prevent.
    /// </remarks>
    private static int? ApplyLoad(string spec, List<RailSpec> rails)
    {
        var (anchorText, currentText, split) = Split(spec);

        if (AnchorOf(anchorText) is not { } anchor)
            return JsonRun.Fail(CliDiagnostics.RailAnchorMalformed("--load", anchorText));

        double? current = null;
        if (split && currentText.Length > 0)
        {
            if (!TryValue(currentText, out double amps))
                return JsonRun.Fail(CliDiagnostics.RailValueMalformed("--load", currentText, "120mA"));
            current = amps;
        }

        var row = new RailLoad { Anchor = anchor, DcCurrentA = current };
        foreach (var rail in rails) Replace(rail.Loads, row, l => l.Anchor);
        return null;
    }

    /// <summary>
    /// <c>--mask file</c> (every observation port on the rail) or <c>--mask U1.VDD=file</c> (one).
    /// </summary>
    /// <remarks>
    /// A mask is per observation port — §2.2's own rule, which is why it lives on the load row rather
    /// than on the rail — so the un-anchored spelling states which ports it means rather than being a
    /// rail-level target with a different name.
    /// </remarks>
    private static int? ApplyMask(string spec, List<RailSpec> rails, RailLengthFormat format)
    {
        var (left, right, split) = Split(spec);
        string file = split ? right : spec;
        string? port = split ? left : null;

        if (!File.Exists(file))
            return JsonRun.Fail(CliDiagnostics.RailMaskNotFound(file));

        List<RailMaskPoint> points;
        try { points = ReadMask(file); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RailMaskUnreadable(file, ex.Message)); }

        if (points.Count < 2)
            return JsonRun.Fail(CliDiagnostics.RailMaskTooShort(file, points.Count));

        var target = RailTarget.OfMask(points);
        bool applied = false;

        foreach (var rail in rails)
            for (int i = 0; i < rail.Loads.Count; i++)
            {
                if (port is not null && !NamesPort(rail.Loads[i].Anchor, port, format))
                    continue;
                rail.Loads[i] = rail.Loads[i] with { Mask = target };
                applied = true;
            }

        // A mask that landed on nothing is a mask that will not be applied, and a silent one reads on
        // the report exactly like a mask that was honoured.
        return applied
            ? null
            : JsonRun.Fail(CliDiagnostics.RailMaskNoPort(
                file, port ?? "(every port)",
                Join([.. rails.SelectMany(r => r.Loads).Select(l => l.Anchor.Describe(format))])));
    }

    /// <summary>
    /// Whether <paramref name="typed"/> names this anchor.
    /// </summary>
    /// <remarks>
    /// <b>Either spelling is accepted, and that is not laxity.</b> The report prints a coordinate
    /// anchor in the BOARD's units now, so that is the spelling a user copies off it — but the
    /// `.crail` itself holds DBU, which is the spelling anything written before this change used and
    /// the only one that is independent of what unit the layout happens to be displayed in. Refusing
    /// one of the two would break a script for a reason that has nothing to do with the script.
    /// </remarks>
    private static bool NamesPort(RailPortAnchor anchor, string typed, RailLengthFormat format) =>
        string.Equals(anchor.Describe(format), typed, StringComparison.OrdinalIgnoreCase)
     || string.Equals(anchor.Describe(), typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Two columns per line — a frequency and a limit, each with its own unit — and
    /// <c>#</c> starts a comment. Base SI on the way in, as every number in a `.crail` is.</summary>
    private static List<RailMaskPoint> ReadMask(string file)
    {
        var points = new List<RailMaskPoint>();
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var fields = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 0) continue;
            if (fields.Length != 2)
                throw new InvalidDataException(
                    $"'{raw.Trim()}' has {fields.Length} field(s). A mask row is a frequency and a limit.");
            if (!TryValue(fields[0], out double hz) || !TryValue(fields[1], out double ohms))
                throw new InvalidDataException($"'{raw.Trim()}' is not a frequency and a limit.");
            points.Add(new RailMaskPoint(hz, ohms));
        }
        return points;
    }

    /// <summary><c>--aggressor "converter=2.2MHz x5"</c>. <c>x</c> and <c>×</c> both spell the
    /// harmonic count, because one of them is not on a keyboard.</summary>
    private static int? ApplyAggressor(string spec, List<RailSpec> rails)
    {
        var (name, rest, split) = Split(spec);
        if (!split || name.Length == 0 || rest.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RailAggressorMalformed(spec));

        int harmonics = 1;
        int mark = rest.IndexOfAny(['x', 'X', '×']);
        if (mark >= 0)
        {
            string tail = rest[(mark + 1)..].Trim();
            if (!int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out harmonics) || harmonics < 1)
                return JsonRun.Fail(CliDiagnostics.RailAggressorMalformed(spec));
            rest = rest[..mark].Trim();
        }

        if (!TryValue(rest, out double hz))
            return JsonRun.Fail(CliDiagnostics.RailValueMalformed("--aggressor", rest, "2.2MHz"));

        var row = new RailAggressor(name, hz, harmonics);
        foreach (var rail in rails)
        {
            int at = rail.Aggressors.FindIndex(
                a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) rail.Aggressors[at] = row; else rail.Aggressors.Add(row);
        }
        return null;
    }

    private static void Replace<T>(List<T> rows, T row, Func<T, RailPortAnchor> anchorOf)
    {
        string key = anchorOf(row).Describe();
        int at = rows.FindIndex(r => string.Equals(anchorOf(r).Describe(), key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) rows[at] = row; else rows.Add(row);
    }

    // ── argument value conventions ───────────────────────────────────────────

    private static (string Left, string Right, bool Split) Split(string text)
    {
        int eq = text.IndexOf('=');
        return eq < 0 ? (text.Trim(), "", false) : (text[..eq].Trim(), text[(eq + 1)..].Trim(), true);
    }

    /// <summary>
    /// <c>U1.VDD</c>, <c>BT1</c>, or <c>@x,y</c> in DBU — the coordinate fallback, which is the
    /// spelling a board with no placement file has (<see cref="RailPortAnchor"/>).
    /// </summary>
    private static RailPortAnchor? AnchorOf(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '@')
        {
            var xy = text[1..].Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2) return null;
            if (!long.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long x)) return null;
            if (!long.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long y)) return null;
            return new RailPortAnchor { Point = (x, y) };
        }

        int dot = text.IndexOf('.');
        return dot <= 0
            ? new RailPortAnchor { Refdes = text }
            : new RailPortAnchor { Refdes = text[..dot], Pin = text[(dot + 1)..] };
    }

    /// <summary>The unit suffix of a value, or the empty string.</summary>
    private static string UnitPartOf(string text)
    {
        text = text.Trim();
        int i = text.Length;
        while (i > 0 && !char.IsAsciiDigit(text[i - 1]) && text[i - 1] != '.') i--;
        return text[i..].Trim();
    }

    /// <summary>
    /// A value with its unit, in BASE SI — <c>120mA</c>, <c>2.2MHz</c>, <c>50mOhm</c>, <c>3.7V</c>.
    /// </summary>
    /// <remarks>
    /// <b>Through <see cref="Units"/>, which is the expression engine's own table</b>, so a spelling
    /// that works in a `.cnl` works here and neither can drift. A bare number is taken as base SI,
    /// which is what every number in a `.crail` already is — the unit is what a person types, not
    /// what the format stores (<c>RailDocumentIo</c>'s own header, and the 2 Hz sweep it cites).
    /// </remarks>
    private static bool TryValue(string text, out double value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        string unit = UnitPartOf(text);
        string number = text[..(text.Length - unit.Length)].Trim();

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
        if (unit.Length == 0) { value = n; return true; }

        string engine = UnitNormalizer.ToEngineUnit(unit);
        if (!Units.IsRecognizedUnit(engine)) return false;

        value = n * (Units.Scale(engine) ?? 1.0);
        return true;
    }

    /// <summary>The drawing layer a name or a <c>layer/datatype</c> key identifies, or null. The same
    /// name map <c>render --layers</c> resolves through, including the layers a document draws on
    /// that the technology does not declare.</summary>
    private static LayerKey? LayerOf(Technology tech, LayoutView view, string name)
    {
        foreach (var l in tech.Layers)
            if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return l.Key;

        foreach (var s in view.Shapes)
            if (string.Equals(s.Layer.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return s.Layer;

        return null;
    }

    private static IReadOnlyList<string> LayerNames(Technology tech) =>
        [.. tech.Layers.Select(l => l.Name.Length > 0 ? l.Name : l.Key.ToString())
                       .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static RailReferenceExtent? ExtentOf(string text) => text.Trim().ToLowerInvariant() switch
    {
        "as-imported" or "as_imported" or "asimported" => RailReferenceExtent.AsImported,
        "filled" or "filled-to-outline"                => RailReferenceExtent.FilledToOutline,
        "infinite"                                     => RailReferenceExtent.Infinite,
        _                                              => null,
    };

    // ── R-rail10-5: the provenance every export carries ──────────────────────

    /// <summary>
    /// What a reader six months later has to know and has no status strip to read it from.
    /// </summary>
    /// <remarks>
    /// <b>Computed once and handed to every writer</b> — the console, the CSV header, the `.npy`
    /// group, the report page and <c>--json</c> — because five writers each assembling their own
    /// would be five chances for one of them to omit the line that matters. Overview §4 rule 1.
    /// </remarks>
    private sealed record RailProvenance(
        string Model,
        string Extent,
        double TemperatureCelsius,
        int PartsWithoutBiasCurve,
        int PartsModelledFromFile,
        bool Indicative,
        string? PartLibraryPath,
        int PartsReferenced,
        string ArtworkPath,
        string TechnologyPath)
    {
        public IReadOnlyList<string> Lines =>
        [
            $"model: {Model} · reference {Extent} · {TemperatureCelsius:0.#} °C",
            PartLibraryPath is null
                ? "no part library resolved: no part is modelled from a file and no bias-curve "
                + "coverage is known"
                // Nothing to count is not a count of nothing. A rail that declares no part rows has
                // no decoupling bank IN THE DOCUMENT, and printing "0 part(s) modelled from a file"
                // there reads as a checked board that came back clean.
                : PartsReferenced == 0
                ? "no part is declared on the rail(s) reported here, so nothing was asked of the "
                + "part library: no bias-curve coverage is known"
                : $"{PartsModelledFromFile} of {PartsReferenced} part number(s) modelled from a file, "
                + $"{PartsWithoutBiasCurve} with no bias curve"
                + (Indicative
                    ? " — some ESRs are class defaults, so any peak height derived from them is INDICATIVE"
                    : ""),
            $"artwork: {ArtworkPath}",
            $"stackup: {TechnologyPath}",
        ];
    }

    private static RailProvenance Provenance(
        RailDocument doc, Options o, BoardInputs? board, string documentPath,
        IReadOnlyList<RailSpec> rails, IReadOnlyList<RailDcResult> results)
    {
        string extent = rails.Select(r => ExtentText(r.ReferenceExtent)).Distinct(StringComparer.Ordinal).ToList() is
            { Count: 1 } one ? one[0] : "mixed across the rails reported here";

        double temperature = results.Count > 0
            ? results[0].Netlist.Provenance.CopperTemperatureCelsius
            : doc.Settings.CopperTemperatureCelsius;

        // Document-relative, like every other reference a `.crail` carries — and through the one
        // function the WINDOW's open calls, so the two surfaces cannot land on different files.
        var library = RailArtwork.ResolvePartLibrary(
            doc, documentPath, out string? resolvedLibraryPath, out string? libraryError);
        string? libraryPath = library is null ? null : resolvedLibraryPath;
        if (libraryError is { Length: > 0 })
        {
            Console.Error.WriteLine(
                $"warning: the part library '{resolvedLibraryPath}' did not read: {libraryError}");
            JsonRun.Note(CliDiagnostics.RailPartLibraryUnreadable(resolvedLibraryPath!, libraryError));
        }

        // R-rail11-6's two headline counts are about THE PARTS ON THIS BOARD, and the board's parts
        // are the rails' own rows — `RailSpec.Parts`, each carrying the internal part number the
        // model attaches to. Asking `PartLibrary.Coverage` about the LIBRARY's own rows instead
        // answers a different question with the same-looking number: a shared library of 500 rows in
        // front of a twelve-part rail prints "412 with no bias curve" and the count reads as a
        // statement about the board. The window computes it over the BOM's part numbers
        // (`RailRfViewModel.RebuildParts`), and a verb whose provenance disagrees with the window's
        // status strip about the same document is exactly the divergence R-rail10-8 exists against.
        //
        // A rail with no part rows at all has a coverage of NOTHING rather than a coverage of zero,
        // which RailProvenance.Lines says out loud — the same rule that governs having no library.
        var referenced = rails
            .SelectMany(r => r.Parts)
            .Select(p => p.PartNumber)
            .Where(pn => !string.IsNullOrWhiteSpace(pn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var coverage = library is not null && referenced.Count > 0 ? library.Coverage(referenced) : null;

        int fromFile = library is null ? 0
            : referenced.Count(pn => library.ResolveModel(pn).Source == PartModelSource.AttachedFile);

        return new RailProvenance(
            o.Model == PdnModelKind.Accurate ? "Accuracy (meshed)" : "Fast",
            extent,
            temperature,
            coverage?.WithoutBiasCurve.Count ?? 0,
            fromFile,
            coverage is { Indicative.Count: > 0 },
            libraryPath,
            referenced.Count,
            board?.ClayPath ?? "(none)",
            board is null ? "(none)" : board.Technology.Name);
    }

    private static string ExtentText(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported      => "as imported",
        RailReferenceExtent.FilledToOutline => "filled to outline — optimistic",
        _                                   => "infinite — an upper bound",
    };

    // ── step 4: the console report (§3.1 — stdout is the result) ─────────────

    private static void Report(
        RailDocument doc, RailDcRunResult run, IReadOnlyList<RailDcResult> results,
        RailOrderResult order, RailProvenance provenance, Options o)
    {
        Console.WriteLine($"railRF:   {(doc.Name.Length > 0 ? doc.Name : "(unnamed)")}");
        foreach (string line in provenance.Lines) Console.WriteLine($"          {line}");
        Console.WriteLine($"Order:    {(order.Order.Count > 0 ? string.Join(" -> ", order.Order) : "(none)")}");
        Console.WriteLine();

        foreach (var result in results)
        {
            Console.WriteLine($"Rail '{result.RailName}'");

            if (result.SourceVoltageV is { } sv) Console.WriteLine($"  source:  {sv:0.####} V");
            if (result.ChainedFrom is { } chain) Console.WriteLine($"  chained: {chain.Describe()}");

            // R-rail14-3 / §9: early and prominently, above the tables rather than under them —
            // the number a designer recognises as wrong at a glance and would never go looking for.
            Console.WriteLine($"  plane C: {result.PlaneCapacitanceLine}");

            Console.WriteLine("  Ports");
            foreach (var port in result.Ports) Console.WriteLine($"    {port.Describe()}");

            if (result.Sources.Count > 1)
            {
                Console.WriteLine("  Sources");
                foreach (var s in result.Sources)
                    Console.WriteLine(
                        $"    {s.Name}: {s.CurrentA * 1e3:0.###} mA ({s.ShareOfTotal:P0})");
            }

            Console.WriteLine("  Where the drop is");
            int shown = o.All ? result.Breakdown.Count : Math.Min(o.Rows, result.Breakdown.Count);
            for (int i = 0; i < shown; i++)
            {
                var row = result.Breakdown[i];
                Console.WriteLine(
                    $"    {row.DropV * 1e3,9:0.###} mV  {row.ShareOfTotal,6:P0}  " +
                    $"{row.ResistanceOhms * 1e3,9:0.###} mOhm  {row.Label}");
            }
            if (shown < result.Breakdown.Count)
                Console.WriteLine($"    ... {result.Breakdown.Count - shown} more row(s) — use --all, or -o report.csv");

            Console.WriteLine(result.ViaCheck.Flags.Count == 0
                ? $"  Vias:    {result.ViaCheck.Transitions.Count} transition(s), none over its limit"
                : $"  Vias:    {result.ViaCheck.Flags.Count} of {result.ViaCheck.Transitions.Count} transition(s) flagged");

            if (result.WithinDropBudget is { } within)
                Console.WriteLine(within ? "  Budget:  met" : "  Budget:  NOT met");

            Console.WriteLine();
        }

        // Findings and notes are kept apart on `em`'s own terms: a finding is something to act on and
        // a note is the run explaining itself, and flattening them is the defect that split exists to
        // fix. Both go to stderr, because stdout is the RESULT.
        foreach (var result in results)
        {
            foreach (string f in result.Findings)
            { Console.Error.WriteLine($"finding: [{result.RailName}] {f}"); JsonRun.Note(CliDiagnostics.RailFinding(result.RailName, f)); }
            foreach (string n in result.Notes)
            { Console.Error.WriteLine($"note: [{result.RailName}] {n}"); JsonRun.Note(CliDiagnostics.RailRunNote(n)); }
        }

        _ = run;
    }

    // ── step 5: what it writes (R-rail10-4) ──────────────────────────────────

    private static int Export(
        Options o, Input input, BoardInputs board, RailDocument doc,
        IReadOnlyList<RailDcResult> results, RailProvenance provenance, DataSet data)
    {
        string output = o.Output!;

        switch (OutputKindOf(output)!.Value)
        {
            case OutputKind.Touchstone:
                // Z(f) at the observation ports is the FREQUENCY answer, and this is the DC phase.
                // Refused rather than written empty: a `.s2p` of a rail is exactly the file a caller
                // would go on to plot, and one holding the DC point repeated would look like a
                // measurement.
                return JsonRun.Fail(CliDiagnostics.RailTouchstoneNotYet(output));

            case OutputKind.Csv:
                return WriteText(output, Csv(doc, results, provenance), "csv");

            case OutputKind.Svg:
            case OutputKind.Pdf:
                return WritePicture(o, input, board, doc, results, provenance);

            default:
            {
                var format = OutputKindOf(output) switch
                {
                    OutputKind.Npy => ExportFormat.Npy,
                    OutputKind.Mat => ExportFormat.Mat,
                    _              => ExportFormat.Tsv,
                };
                try
                {
                    EnsureDirectory(output);
                    DataSetExporter.Export(data, output, format);
                }
                catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RailWriteFailed(output, ex.Message)); }

                JsonRun.AddOutput(format.ToString().ToLowerInvariant(), output);
                Console.WriteLine($"Wrote {output}");
                return 0;
            }
        }
    }

    private static int WriteText(string path, string text, string kind)
    {
        try
        {
            EnsureDirectory(path);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RailWriteFailed(path, ex.Message)); }

        JsonRun.AddOutput(kind, path);
        Console.WriteLine($"Wrote {path}");
        return 0;
    }

    private static void EnsureDirectory(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// §11.5's CSV: the ranked breakdown, the via flags, the parts and the ports.
    /// </summary>
    /// <remarks>
    /// <b>The provenance is a comment header</b> (R-rail10-5) — the CSV's own spelling of what the
    /// `.npy` carries as a group and the page carries in its banner.
    ///
    /// <para><b>The anti-resonance table is named and empty.</b> §11.5 lists it and it is a frequency
    /// answer; saying so costs one line and is the difference between "this board has none" and "this
    /// phase does not compute them", which a reader of a CSV cannot otherwise tell apart.</para>
    /// </remarks>
    private static string Csv(RailDocument doc, IReadOnlyList<RailDcResult> results, RailProvenance provenance)
    {
        var sb = new StringBuilder();
        sb.Append("# circuitRF railRF — DC\n");
        sb.Append($"# document,{Q(doc.Name)}\n");
        foreach (string line in provenance.Lines) sb.Append($"# {line}\n");

        sb.Append("\nsection,rail,label,value,unit,detail\n");

        foreach (var r in results)
        {
            foreach (var p in r.Ports)
            {
                sb.Append($"port,{Q(r.RailName)},{Q(p.Name)},{F(p.VoltageV)},V,{Q(p.Describe())}\n");
                if (p.CurrentA is { } i)
                    sb.Append($"port-current,{Q(r.RailName)},{Q(p.Name)},{F(i)},A,\n");
            }

            foreach (var s in r.Sources)
                sb.Append($"source,{Q(r.RailName)},{Q(s.Name)},{F(s.CurrentA)},A,{F(s.ShareOfTotal)} share\n");

            foreach (var row in r.Breakdown)
                sb.Append($"breakdown,{Q(r.RailName)},{Q(row.Label)},{F(row.DropV)},V," +
                          $"{F(row.ResistanceOhms)} Ohm / {F(row.CurrentA)} A / {row.ElementCount} element(s)\n");

            foreach (var t in r.ViaCheck.Transitions)
                sb.Append(
                    $"via,{Q(r.RailName)},{Q($"{t.FromLayer} to {t.ToLayer}")}," +
                    $"{F(t.Worst?.CurrentA ?? 0)},A," +
                    $"{t.Count} barrel(s) / {F(t.TotalCurrentA)} A total / peaking {F(t.PeakingFactor)}\n");

            foreach (var flag in r.ViaCheck.Flags)
                sb.Append($"via-flag,{Q(r.RailName)},{Q(flag.Describe())},,,\n");

            foreach (string n in r.ViaCheck.Notes)
                sb.Append($"via-note,{Q(r.RailName)},,,,{Q(n)}\n");

            foreach (var reg in r.Regulators)
                sb.Append($"regulator,{Q(r.RailName)},{Q(reg.Refdes)},{F(reg.InputVoltageV)},V,{Q(reg.Describe())}\n");

            foreach (string f in r.Findings) sb.Append($"finding,{Q(r.RailName)},,,,{Q(f)}\n");
            foreach (string n in r.Notes)    sb.Append($"note,{Q(r.RailName)},,,,{Q(n)}\n");
        }

        sb.Append("# anti-resonances: none are reported here. They are a FREQUENCY answer and this "
                + "is the DC phase — an empty table here does not mean this board has none.\n");
        return sb.ToString();

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// The report, drawn by <see cref="RailReportPage"/> — the one route from a rail result to a
    /// page (§11.7), which is also what <c>Report ▸</c> calls.
    /// </summary>
    /// <remarks>
    /// <b>The bytes are complete before anything reaches the filesystem</b>, exactly as
    /// <c>render</c>'s own <c>Emit</c> arranges it — which is what makes R-rail10-6's "a cancelled
    /// run writes nothing" true by construction rather than by a guard someone has to remember.
    /// </remarks>
    private static int WritePicture(
        Options o, Input input, BoardInputs board, RailDocument doc,
        IReadOnlyList<RailDcResult> results, RailProvenance provenance)
    {
        var request = new RailReportPageRequest
        {
            Title      = doc.Name.Length > 0 ? doc.Name : Path.GetFileNameWithoutExtension(input.DocumentPath),
            Provenance = provenance.Lines,
            Sections   = Sections(results, o),
            Board      = board.View,
            Technology = board.Technology,
            // §2.4's headline, which is the tab the window opens on and the one a report is about.
            Map        = RailMapScene.Build(results.Count > 0 ? results[0] : null,
                                            RailMapKind.Drop, board.View.DbuPerMicron),
            Theme      = ThemeResolver.Resolve(ThemeResolver.DefaultThemeName,
                                               Path.GetDirectoryName(input.DocumentPath)),
            Variant    = ColorVariant.Light,
            BaseDir    = board.BaseDir,
        };

        bool pdf = OutputKindOf(o.Output!) == OutputKind.Pdf;
        byte[] bytes;
        try
        {
            // One encoder for every picture this program writes — see VectorPage, and R-rail10-8's
            // "no second export path". Nothing here draws; the delegate is the only thing that does.
            bytes = pdf
                ? VectorPage.Pdf(PageW, PageH, canvas => RailReportPage.Draw(canvas, request, PageW, PageH))
                : VectorPage.Svg(PageW, PageH, canvas => RailReportPage.Draw(canvas, request, PageW, PageH));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RailWriteFailed(o.Output!, ex.Message)); }

        RunHost.Cancellation.ThrowIfCancellationRequested();

        try
        {
            EnsureDirectory(o.Output!);
            File.WriteAllBytes(o.Output!, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RailWriteFailed(o.Output!, ex.Message)); }

        JsonRun.AddOutput(pdf ? "pdf" : "svg", o.Output!);
        Console.WriteLine($"Wrote {o.Output} ({PageW}x{PageH} points, {bytes.Length:N0} bytes)");
        return 0;
    }

    /// <summary>The report page's text blocks. Every line is the RESULT's own sentence — see
    /// <see cref="RailReportPage"/>'s header for why none of them is re-worded here.</summary>
    private static IReadOnlyList<RailReportSection> Sections(IReadOnlyList<RailDcResult> results, Options o)
    {
        var sections = new List<RailReportSection>();

        foreach (var r in results)
        {
            sections.Add(new RailReportSection(
                $"Rail '{r.RailName}' — ports", [.. r.Ports.Select(p => p.Describe())]));

            int shown = o.All ? r.Breakdown.Count : Math.Min(o.Rows, r.Breakdown.Count);
            sections.Add(new RailReportSection(
                $"Rail '{r.RailName}' — where the drop is",
                [.. r.Breakdown.Take(shown).Select(
                     row => $"{row.DropV * 1e3:0.###} mV ({row.ShareOfTotal:P0}) · {row.Label}")]));

            if (r.ViaCheck.Transitions.Count > 0)
                sections.Add(new RailReportSection(
                    $"Rail '{r.RailName}' — vias",
                    r.ViaCheck.Flags.Count == 0
                        ? [$"{r.ViaCheck.Transitions.Count} transition(s), none over its limit"]
                        : [.. r.ViaCheck.Flags.Select(f => f.Describe())]));

            if (r.Findings.Count > 0)
                sections.Add(new RailReportSection($"Rail '{r.RailName}' — findings", r.Findings));
        }

        return sections;
    }

    // ── the DataSet, and the provenance group it carries (R-rail10-5) ────────

    /// <summary>The group an <c>.npy</c> or a <c>.mat</c> carries the provenance in — <c>em</c>'s
    /// diagnostics group is the precedent, and the name is said once here.</summary>
    private const string ProvenanceGroup = "provenance";

    /// <summary>
    /// Every rail's own <see cref="DataSet"/>, grouped by rail name, plus the provenance.
    /// </summary>
    /// <remarks>
    /// <b>Nothing invents a result type</b> — each rail's cubes are the ones
    /// <c>DcResultPacker</c> already packed, moved into a group named after the rail so two rails of
    /// one board do not collide. The provenance rides as a labelled AXIS rather than as prose,
    /// because that is the only string a `.npy` carries and a reader that can find the numbers can
    /// find the labels beside them.
    /// </remarks>
    private static DataSet Pack(IReadOnlyList<RailDcResult> results, RailProvenance provenance)
    {
        var merged = new DataSet();

        foreach (var r in results)
            foreach (string group in r.Data.Groups)
                foreach (var (name, cube) in r.Data.CubesIn(group))
                    merged.AddToGroup(
                        group.Length == 0 ? r.RailName : $"{r.RailName}.{group}", name, cube);

        var lines = provenance.Lines;
        var axis = new Axis("provenance", [.. Enumerable.Range(0, lines.Count).Select(i => (double)i)],
                            "index", [.. lines]);
        merged.AddToGroup(ProvenanceGroup, "Lines", new DataCube([axis], new double[lines.Count]));
        merged.AddToGroup(ProvenanceGroup, "TemperatureC", DataCube.Scalar(provenance.TemperatureCelsius));
        merged.AddToGroup(ProvenanceGroup, "PartsWithoutBiasCurve",
                          DataCube.Scalar(provenance.PartsWithoutBiasCurve));
        merged.AddToGroup(ProvenanceGroup, "PartsModelledFromFile",
                          DataCube.Scalar(provenance.PartsModelledFromFile));
        merged.AddToGroup(ProvenanceGroup, "Indicative", DataCube.Scalar(provenance.Indicative ? 1.0 : 0.0));

        return merged;
    }

    private static RailReportJson RailJson(
        RailDocument doc, RailDcRunResult run, IReadOnlyList<RailDcResult> results,
        RailOrderResult order, RailProvenance provenance, PdnModelKind model)
        => new(
            doc.Name,
            model == PdnModelKind.Accurate ? "accurate" : "fast",
            provenance.Extent,
            provenance.TemperatureCelsius,
            provenance.PartsWithoutBiasCurve,
            provenance.PartsModelledFromFile,
            provenance.Indicative,
            order.Order,
            [.. results.Select(r => new RailResultJson(
                r.RailName,
                r.SourceVoltageV,
                r.WithinDropBudget,
                [.. r.Ports.Select(p => new RailPortJson(p.Name, p.VoltageV, p.DropV, p.CurrentA, p.IsObservationOnly))],
                [.. r.Breakdown.Select(b => new RailBreakdownJson(
                    b.Label, b.DropV, b.ShareOfTotal, b.ResistanceOhms, b.CurrentA, b.ElementCount))],
                r.ViaCheck.Transitions.Count,
                r.ViaCheck.Flags.Count,
                r.Findings))],
            [.. run.Rails.Select(r => r.RailName)]);

    private static void Progress(string stage) => Console.Error.WriteLine($"[circuitRF] {stage}...");

    private static string Join(IReadOnlyList<string> items)
        => items.Count == 0 ? "(none)" : string.Join(", ", items);
}
