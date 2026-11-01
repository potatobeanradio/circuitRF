// `circuitrf recognize <path>` — a board's artwork as a circuit, headlessly (brief-artsch-7-cli-and-mcp.md;
// docs/design/cli.md §26; docs/design/artwork-to-schematic.md).
//
// THIS FILE OWNS NO RECOGNITION, and it must never start (R-as7-1). Every decision — ground, vias, ports,
// parts, lines, names, the drawing, the target's rules — is ArtworkRecognition's (src/Design/Layout/
// Recognition), the function the GUI's Create Schematic from Artwork calls. What is here is argument parsing,
// refusals and reporting, on Authoring.cs' terms: an operation that lives only in a verb is not a capability,
// and a verb that re-implements one diverges from it silently. A comment-stripped source scan holds it: src/Cli
// names nothing in that namespace but the entry point, its options, its result and PartsTableCsv.
//
// READ-ONLY BY DEFAULT (R-as7-3). With neither -o nor --into nothing is written: the report and the parts table
// go to stdout. That is the first call an agent makes — review the parts, then write.
//
// THE TARGETS ARE WRITTEN LAST (R-as7-6), after the recognition and the emit have finished, so a cancelled run
// (130) or a refused one (1) has written nothing. Never 2: nothing here solves a circuit.
//
// A PICTURE is read where a layout is (brief-img-11 R-im11-2): a layout picture by ImageRecognition (the trace, then
// this same artwork recognition), a schematic picture by SchematicImageRecognition — the functions Create Schematic
// from Image calls, and nothing else. The one thing written regardless is --overlay, the picture with what was read
// drawn over it: written even when the reading is refused, because that is when it is most useful.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Design.Workspace;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static partial class Recognize
{
    private const string Verb = "recognize";

    private sealed class Options
    {
        public string? Path, Cell, Output, Into, PartsOut, Parts, Bom, Placement, Region, Ground, GroundAt, Tech;
        public readonly PictureFlags Picture = new();
        /// <summary>The pictures after the first (brief-img-14 R-im14-14): several pictures are one plot session.</summary>
        public readonly List<string> Pictures = [];
        public readonly PlotFlags Plot = new();
        public bool Replace;
        public PlacementOrigin? PlacementOrigin;
        public LayoutUnit? PlacementUnit;
        public ViaPolicy? Vias;
        public CoplanarReading? Coplanar;
        public double? CoplanarFactor;
        public RecognitionFrequency? Start, Stop;
        public int? Npts;
        public int? Digits;
        public bool FreeOrientation;
        /// <summary><c>--arrange board</c> — a layout's alone; <c>tidy</c> is either kind's and is held by the picture flags.</summary>
        public bool ArrangeBoard;
    }

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;
        if (o.Path is null) { JsonRun.Report(CliDiagnostics.RecognizePathRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;
        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.RecognizePathNotFound(o.Path));

        if (o.ArrangeBoard && (o.Pictures.Count > 0 || DocumentKinds.Classify(o.Path) == DocumentKind.Picture))
            return JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--arrange", "board", "drawn or tidy for a schematic picture; board is a layout's"));
        if (o.Pictures.Count > 0)
        {
            try { return RecognisePictures(o); }
            catch (OperationCanceledException)
            {
                JsonRun.Report(CliDiagnostics.RecognizeCancelled());
                return 130;
            }
        }
        if (DocumentKinds.Classify(o.Path) == DocumentKind.Picture)
        {
            try { return RecognisePicture(o); }
            catch (OperationCanceledException)
            {
                JsonRun.Report(CliDiagnostics.RecognizeCancelled());
                return 130;
            }
        }
        // --arrange is a layout's too (R-dfr22-2-1): board or tidy; drawn is a schematic picture's alone.
        if (o.Picture.Arrange == SchematicArrange.AsDrawn)
            return JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--arrange", "drawn", "board or tidy for a layout"));
        if ((o.Picture.All.FirstOrDefault(f => f != "--arrange") ?? (o.Tech is null ? null : "--tech")) is { } pictureFlag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, pictureFlag, "a picture", "a layout"));
        if (o.Plot.Given.FirstOrDefault() is { } plotFlag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, plotFlag, "a plot picture", "a layout"));
        if (o.Output is { } cnlOut && !cnlOut.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotCnl(cnlOut));

        var (clay, refusal) = ResolveLayout(o);
        if (refusal is { } no) return no;

        try
        {
            return Recognise(o, clay!);
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.RecognizeCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf recognize <file.clay | cell folder | workspace --cell N | picture | plot picture...>\n" +
            "                 [-o circuit.cnl] [--into new:<name> | --into artwork] [--replace]\n" +
            "                 [--parts-out parts.csv] [--parts parts.csv] [--bom file] [--placement file]\n" +
            "                 [--placement-origin symbol|body|pin1] [--placement-unit mm|mil|in]\n" +
            "                 [--region x0,y0,x1,y1] [--ground <net> | --ground-at x,y] [--vias model|ground]\n" +
            "                 [--coplanar auto|microstrip|gcpw] [--coplanar-factor k]\n" +
            "                 [--start f] [--stop f] [--npts n] [--digits n] [--free-orientation] [--arrange board|tidy]\n" +
            "  a picture:     [--image-kind auto|schematic|layout] [--tech <id|path.ctech>] [--overlay out.png]\n" +
            "                 [--underlay on|off]\n" +
            "    layout:      --scale <len>/px | --dpi <n> | --scale-points x0,y0,x1,y1=<len> | --scale auto\n" +
            "                 [--layer-map \"#rrggbb=<layer>|drill|outline|silk|ignore,...\"] [--preset <name>]\n" +
            "                 [--region x0,y0,x1,y1 in px] [--min-feature px2] [--simplify px] [--edge-stroke px] [--snap-deg d] [--snap45 on|off]\n" +
            "    schematic:   [--crossings never|dot|always] [--arrange drawn|tidy] [--connect x,y] [--disconnect x,y]\n" +
            "                 [--threshold k] [--profile auto|measured|enlarged|crossings|bold]\n" +
            "                 [--pen px] [--text-height px] [--symbol-scale px]\n" +
            "    plot:        [-o part.s2p] [--csv traces.csv] [--picture more.png] [--plot n,n] [--keep-sources on|off]\n" +
            "                 [--x-axis <px>=<value>,<px>=<value>[@plot]] [--y-axis ...] [--y2-axis ...] [--x-log] [--y-log] [--y2-log]\n" +
            "                 [--trace <T3|#rrggbb|dashed>[@plot]=S21:db[,axis=left|right]] [--smith-z0 50Ohm] [--freq <trace>=<start>..<stop>]\n" +
            "                 [--phase S21=min|zero|delay:<t>] [--fill S12=S21,S22=zero] [--grid <start>..<stop>/<n>[log]]\n" +
            "                 [--prefer S11=chart|rect|T3] [--format db|ma|ri] [--ports n] [--all-traces]\n" +
            "                 [--fit <T3|#rrggbb|dashed|all>[@plot]=linear|exp|poly[:n]|spline[:rms]|none] [--probe 2.4GHz]\n" +
            "    by hand:     --manual-box <x0px,y0px,x1px,y1px>[@deg] --x-range 100MHz..10GHz[:log] --y-range -40..0[:log]\n" +
            "                 [--y2-range -180..180[:log]] [--curve \"<name>=<px>,<py>;<px>,<py>;…[,axis=right][,is=S21:db]\"]\n" +
            "                 [--follow <name>=<px>,<py>[,axis=right][,is=S11:db]] [--sample 101 | --sample-step 50MHz] [--method linear|smooth]\n" +
            "  Writes nothing unless -o, --into, --parts-out or --overlay is given. Layout coordinates carry a unit\n" +
            "  (um, mm, mil); picture coordinates are pixels and say so (px).");
        return 1;
    }

    // ── arguments ────────────────────────────────────────────────────────────

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (o.Plot.Take(Verb, args, ref i) is { } plotTook)
            {
                if (plotTook != 0) return plotTook;
                continue;
            }
            // "board" is a layout's arrangement alone (R-dfr22-2-1); "drawn" and "tidy" are read with the picture flags.
            if (args[i] == "--arrange" && i + 1 < args.Length && args[i + 1].Equals("board", StringComparison.OrdinalIgnoreCase))
            {
                o.ArrangeBoard = true;
                i++;
                continue;
            }
            if (o.Picture.Take(Verb, args, ref i, schematic: true) is { } took)
            {
                if (took != 0) return took;
                continue;
            }
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            int? Bad(string expected) => JsonRun.Fail(CliDiagnostics.RecognizeBadValue(a, args[i], expected));
            switch (a)
            {
                case "-o" or "--output" when hasValue: o.Output    = args[++i]; continue;
                case "--cell"            when hasValue: o.Cell      = args[++i]; continue;
                case "--into"            when hasValue: o.Into      = args[++i];
                    if (o.Into != "artwork" && !(o.Into.StartsWith("new:", StringComparison.Ordinal) && o.Into.Length > 4))
                        return Bad("new:<name> or artwork");
                    continue;
                case "--replace":                       o.Replace   = true; continue;
                case "--parts-out"       when hasValue: o.PartsOut  = args[++i]; continue;
                case "--parts"           when hasValue: o.Parts     = args[++i]; continue;
                case "--bom"             when hasValue: o.Bom       = args[++i]; continue;
                case "--placement"       when hasValue: o.Placement = args[++i]; continue;
                case "--region"          when hasValue: o.Region    = args[++i]; continue;
                case "--ground"          when hasValue: o.Ground    = args[++i]; continue;
                case "--ground-at"       when hasValue: o.GroundAt  = args[++i]; continue;
                case "--tech"            when hasValue: o.Tech      = args[++i]; continue;
                case "--picture"         when hasValue: o.Pictures.Add(args[++i]); continue;

                case "--placement-origin" when hasValue:
                    o.PlacementOrigin = args[++i].ToLowerInvariant() switch
                    {
                        "symbol" => PlacementOrigin.SymbolOrigin,
                        "body"   => PlacementOrigin.BodyCentre,
                        "pin1"   => PlacementOrigin.PinOne,
                        _        => null,
                    };
                    if (o.PlacementOrigin is null) return Bad("symbol, body or pin1");
                    continue;
                case "--placement-unit" when hasValue:
                    o.PlacementUnit = args[++i].ToLowerInvariant() switch
                    {
                        "mm"           => LayoutUnit.Mm,
                        "mil"          => LayoutUnit.Mil,
                        "in" or "inch" => LayoutUnit.Inch,
                        _              => null,
                    };
                    if (o.PlacementUnit is null) return Bad("mm, mil or in");
                    continue;
                case "--vias" when hasValue:
                    o.Vias = args[++i].ToLowerInvariant() switch
                    {
                        "model"  => ViaPolicy.Model,
                        "ground" => ViaPolicy.Ground,
                        _        => null,
                    };
                    if (o.Vias is null) return Bad("model or ground");
                    continue;
                case "--coplanar" when hasValue:
                    o.Coplanar = args[++i].ToLowerInvariant() switch
                    {
                        "auto"       => CoplanarReading.Auto,
                        "microstrip" => CoplanarReading.Microstrip,
                        "gcpw"       => CoplanarReading.Gcpw,
                        _            => null,
                    };
                    if (o.Coplanar is null) return Bad("auto, microstrip or gcpw");
                    continue;
                case "--coplanar-factor" when hasValue:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double k) || !(k > 0))
                        return Bad("a positive number of substrate heights");
                    o.CoplanarFactor = k;
                    continue;
                case "--npts" when hasValue:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2)
                        return Bad("a whole number of points, at least 2");
                    o.Npts = n;
                    continue;
                case "--free-orientation":
                    o.FreeOrientation = true;
                    continue;
                case "--digits" when hasValue:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
                        || d is < 1 or > RecognitionEmitOptions.AllDigits)
                        return Bad($"a whole number of significant digits, 1 to {RecognitionEmitOptions.AllDigits}");
                    o.Digits = d;
                    continue;
                case "--start" when hasValue:
                    if (RecognitionSweep.Parse(args[++i]) is not { } start) return Bad("a frequency with its unit (Hz, kHz, MHz, GHz)");
                    o.Start = start;
                    continue;
                case "--stop" when hasValue:
                    if (RecognitionSweep.Parse(args[++i]) is not { } stop) return Bad("a frequency with its unit (Hz, kHz, MHz, GHz)");
                    o.Stop = stop;
                    continue;

                default:
                    if (a.StartsWith('-')) { JsonRun.Report(CliDiagnostics.RecognizeUnknownOption(a)); return Usage(); }
                    // A second path is a second picture of a plot session (R-im14-14); RecognisePictures checks it is one.
                    if (o.Path is not null) { o.Pictures.Add(a); continue; }
                    o.Path = a;
                    continue;
            }
        }

        // -o is a .cnl, or a Touchstone for plot pictures; which one the input takes is decided once its kind is known.
        if (o.Output is { } output && !output.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase) && !IsTouchstone(output))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotCnl(output));
        return null;
    }

    // ── which layout (R-as7-2) ───────────────────────────────────────────────

    private static (string? Clay, int? Refusal) ResolveLayout(Options o)
    {
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);
        if (o.Cell is not null && kind != DocumentKind.Workspace)
            return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--cell", o.Cell, "a cell name, and only with a workspace path")));

        switch (kind)
        {
            case DocumentKind.Layout:
                return (Path.GetFullPath(path), null);
            case DocumentKind.Cell:
                return LayoutOf(Path.GetFullPath(path));
            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (o.Cell is null) return (null, JsonRun.Fail(CliDiagnostics.RecognizeCellRequired(path)));
                var found = CellLookup.Find(root, o.Cell);
                if (found.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoSuchCell(path, o.Cell, Join(CellLookup.Names(root)))));
                if (found.Count > 1)
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeAmbiguousCell(o.Cell, Join(found))));
                return LayoutOf(found[0]);
            }
            case DocumentKind.Interchange:
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(path, DocumentKinds.InterchangeFormat(path) ?? "interchange")));
            default:
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(path, DocumentKinds.Name(kind))));
        }
    }

    /// <summary>A cell folder's primary layout — <c>CellFolder.ResolvePrimary</c>'s answer and no other.</summary>
    private static (string? Clay, int? Refusal) LayoutOf(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.ResolvedName is { Length: > 0 } name)
            return (Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name), null);
        if (primary.State == PrimaryState.NoView)
            return (null, JsonRun.Fail(CliDiagnostics.RecognizeNoLayout(cellDir, "a cell folder with no layout view")));
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.clay").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList() : [];
        return (null, JsonRun.Fail(CliDiagnostics.RecognizeLayoutAmbiguous(cellDir, Join(files!))));
    }

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);

    // ── the run ──────────────────────────────────────────────────────────────

    private static int Recognise(Options o, string clay)
    {
        var input = RecognitionInput.FromFile(clay);
        int dbu = input.View.DbuPerMicron;

        // ── the companions, read with their own refusals (an unstated origin names --placement-origin) ──
        if (o.Placement is { } placementPath)
        {
            if (!File.Exists(placementPath)) return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--placement", placementPath));
            if (PlacementFile.ReadFile(placementPath, dbu, o.PlacementOrigin, o.PlacementUnit) is not { } placement)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--placement", placementPath));
            input = input with { Placement = placement };
        }
        if (o.Bom is { } bomPath)
        {
            if (!File.Exists(bomPath) || RecognitionBom.ReadFile(bomPath) is not { } bom)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--bom", bomPath));
            input = input with { Bom = bom };
        }
        if (o.Parts is { } partsPath)
        {
            if (!File.Exists(partsPath)) return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--parts", partsPath));
            input = input with { PartsCsvPath = Path.GetFullPath(partsPath) };
        }

        // ── scope and options: each absent flag is the dialog's default ─────────────────────────────
        if (o.Region is { } region)
        {
            var (r, refusal) = Coordinates(region, "--region", 4, dbu);
            if (refusal is { } no) return no;
            input = input with { Scope = RecognitionScope.Rectangle(r![0], r[1], r[2], r[3]) };
        }
        var options = input.Options;
        if (o.GroundAt is { } at)
        {
            var (p, refusal) = Coordinates(at, "--ground-at", 2, dbu);
            if (refusal is { } no) return no;
            options = options with { GroundAt = (p![0], p[1]) };
        }
        if (o.Ground is { } net)               options = options with { GroundNet = net };
        if (o.Vias is { } vias)                options = options with { Vias = vias };
        if (o.Coplanar is { } coplanar)        options = options with { Coplanar = coplanar };
        if (o.CoplanarFactor is { } factor)    options = options with { CoplanarGapFactor = factor };
        if (o.Stop is { } top)                 options = options with { TopFrequencyHz = top.Hz };
        input = input with { Options = options };

        var emit = new RecognitionEmitOptions { Sweep = Sweep(o, input), Digits = o.Digits ?? RecognitionEmitOptions.DefaultDigits,
                                              ArtworkSides = !o.FreeOrientation,
                                              Arrange = o.Picture.Arrange == SchematicArrange.Tidy ? ArtworkArrange.Tidy : ArtworkArrange.Board };

        // ── recognise and emit; --into writes the schematic, and only after everything else succeeded ─
        RecognitionResult result;
        RecognitionCircuit? circuit;
        string? schematic = null, cell = null;
        bool checkpoint = false;
        if (o.Into is { } into)
        {
            var (target, refusal) = Target(o, into, clay);
            if (refusal is { } no) return no;
            Console.Error.WriteLine($"[circuitRF] recognising {clay}");
            var run = ArtworkRecognition.Run(input, target!, new RecognitionRunOptions { Emit = emit }, RunHost.Control);
            if (run.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.RecognizeRefused(why));
            (result, circuit, schematic, cell, checkpoint) = (run.Result, run.Circuit, run.SchematicPath, run.CellDir, run.CheckpointTaken);
        }
        else
        {
            Console.Error.WriteLine($"[circuitRF] recognising {clay}");
            (result, circuit) = ArtworkRecognition.Circuit(input, emit, RunHost.Control);
            if (circuit is null) return JsonRun.Fail(CliDiagnostics.RecognizeRefused(result.Refusal ?? "The artwork could not be read."));
        }

        return Conclude(o, clay, input.TechnologyPath, result.Report, result.Parts, circuit!.TestBench.Instances.Count,
                        dir => circuit.CnlText(dir), schematic, cell, checkpoint, null);
    }

    /// <summary>The other two writes — <c>-o</c> and <c>--parts-out</c>, after the recognition and any <c>--into</c> — and
    /// the report: stdout is the result, <c>result.recognize</c> its document. Shared by a layout and both kinds of
    /// picture: what differs is only what was read.</summary>
    private static int Conclude(Options o, string source, string? technology, RecognitionReport report, PartsTable parts, int instances,
                                Func<string, string> cnlText, string? schematic, string? cell, bool checkpoint,
                                Func<RecognizeReportJson, RecognizeReportJson>? picture)
    {
        string? netlist = null, partsOut = null;
        if (o.Output is { } output)
        {
            netlist = Path.GetFullPath(output);
            if (Write(netlist, () => File.WriteAllText(netlist, cnlText(Path.GetDirectoryName(netlist)!))) is { } failed)
                return failed;
        }
        if (o.PartsOut is { } partsTarget)
        {
            partsOut = Path.GetFullPath(partsTarget);
            if (Write(partsOut, () => PartsTableCsv.WriteFile(partsOut, parts)) is { } failed) return failed;
        }

        var text = new StringBuilder();
        foreach (string line in report.Lines()) text.Append(line).Append('\n');
        text.Append('\n').Append(PartsTableCsv.Write(parts));
        if (schematic is not null) { text.Append('\n').Append("schematic: ").Append(schematic).Append('\n'); JsonRun.AddOutput("schematic", schematic); }
        if (netlist is not null)   { text.Append("netlist: ").Append(netlist).Append('\n');   JsonRun.AddOutput("netlist", netlist); }
        if (partsOut is not null)  { text.Append("parts: ").Append(partsOut).Append('\n');    JsonRun.AddOutput("parts", partsOut); }
        Console.Write(text.ToString());

        var json = Document(source, technology, o.Region, instances, report, parts) with
        {
            Schematic = schematic, Cell = cell, Netlist = netlist, PartsTable = partsOut, CheckpointTaken = checkpoint,
        };
        JsonRun.Recognize = picture is null ? json : picture(json);
        return 0;
    }

    /// <summary><c>result.recognize</c> before anything is written: what was read, the report and the parts table.</summary>
    private static RecognizeReportJson Document(string source, string? technology, string? region, int instances, RecognitionReport report,
                                                PartsTable parts) => new(
        source, technology, region ?? "whole", instances,
        [.. report.Findings.Select(f => new RecognizeFindingJson(
            f.Class.ToString(), f.Count, f.Sentence,
            [.. f.Anchors.Select(a => new RecognizeAnchorJson(a.X, a.Y, a.Layer is { } l ? $"{l.Layer}/{l.Datatype}" : null))]))],
        [.. parts.Rows.OrderBy(r => r.Refdes, PartsTable.NaturalOrder).Select(r =>
            (IReadOnlyDictionary<string, string>)PartsTableCsv.Columns.ToDictionary(c => c, c => PartsTableCsv.Cell(parts, r, c)))],
        null, null, null, null, false);

    // ── a picture (brief-img-11 R-im11-2) ─────────────────────────────────────────────────────────────

    /// <summary>The kind decides the reader: IM-2's reading, or <c>--image-kind</c>'s. A picture of no drawing is refused
    /// with IM-2's sentence and the flag that overrides it — after its overlay (the bare picture) is written.</summary>
    private static int RecognisePicture(Options o)
    {
        string path = Path.GetFullPath(o.Path!);
        if (o.Cell is not null) return JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--cell", o.Cell, "a cell name, and only with a workspace path"));
        if (o.Into == "artwork") return JsonRun.Fail(CliDiagnostics.PictureIntoArtwork());
        var decoded = ImageSource.FromFile(path);
        if (decoded.Source is not { } source) return JsonRun.Fail(CliDiagnostics.PictureUnreadable(Verb, path, decoded.Refusal ?? "not a picture"));

        var read = ImageKind.Classify(source.Raster);
        var kind = o.Picture.Kind is { } forced ? read.Force(forced) : read;
        // Four corners say the picture is a photo of a board (IM-13), whatever it read as.
        if (o.Picture.HasCorners && kind.Kind is DrawingKind.None or DrawingKind.Schematic && o.Picture.Kind is null)
            kind = read.Force(DrawingKind.BoardPhoto);
        // A plot read by hand needs nothing read of the picture to say it is one (brief-pdg-4 §4).
        if (o.Plot.Manual && kind.Kind != DrawingKind.Plot && o.Picture.Kind is null) kind = read.Force(DrawingKind.Plot);
        if (kind.Kind == DrawingKind.Plot) return RecognisePlots(o, [(path, source, kind)]);
        if (o.Plot.Given.FirstOrDefault() is { } plotFlag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, plotFlag, "a plot picture", $"a {kind.Name} picture"));
        if (o.Output is { } cnlOut && !cnlOut.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotCnl(cnlOut));
        var image = PictureFlags.ImageJson(source, kind);
        Console.Error.WriteLine($"[circuitRF] reading {path} as a {kind.Name} picture");
        if (kind.Kind == DrawingKind.None)
        {
            o.Picture.WriteOverlay(Verb, ImageOverlay.Empty, source);
            JsonRun.Recognize = Document(path, null, o.Region, 0, new RecognitionReport(), PartsTable.Empty) with { Image = image };
            return JsonRun.Fail(CliDiagnostics.PictureNoDrawing(Verb, path, kind.Reason ?? "nothing drawn"));
        }

        // Every flag belongs to one kind of input; one handed to the other kind is refused, never ignored.
        bool schematic = kind.Kind == DrawingKind.Schematic;
        string given = schematic ? "a schematic picture" : "a layout picture";
        var wrong = schematic
            ? o.Picture.LayoutFlags.Concat(LayoutOnly(o))
            : o.Picture.SchematicFlags;
        if (wrong.FirstOrDefault() is { } flag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, flag, schematic ? "a layout picture or a layout" : "a schematic picture", given));

        var (tech, techPath, _, techRefusal) = schematic && o.Tech is null
            ? (Tech: (Technology?)null, Path: (string?)null, CatalogId: (string?)null, Refusal: (int?)null)
            : PictureFlags.Technology(Verb, o.Tech, path, walkUp: true);
        if (techRefusal is { } noTech) return noTech;
        if (schematic && o.Tech is null)
        {
            // A schematic picture needs no technology; a workspace's, when there is one, gives a physical line its εeff.
            var (res, _) = TechnologyResolver.ResolveForDocument(null, path, null, new TechnologyCache());
            (tech, techPath) = (res.Tech, res.Tech is null ? null : res.ResolvedPath);
        }
        if (o.Parts is { } parts && !File.Exists(parts)) return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--parts", parts));
        var emit = new RecognitionEmitOptions
        {
            Sweep = RecognitionSweep.Compose(null, o.Start, o.Stop, o.Npts), Digits = o.Digits ?? RecognitionEmitOptions.DefaultDigits,
            ArtworkSides = !o.FreeOrientation,
        };
        return schematic
            ? RecogniseSchematicPicture(o, path, source, kind, image, tech, techPath, emit)
            : RecogniseLayoutPicture(o, path, source, kind, image, tech!, techPath, emit);
    }

    /// <summary>Several pictures (brief-img-14 R-im14-14): every one must be a picture, and all are read as plots into one
    /// session — no other kind of picture takes several. One that reads as another kind is read as a plot and says so.</summary>
    private static int RecognisePictures(Options o)
    {
        if (o.Picture.Kind is { } forced && forced != DrawingKind.Plot)
            return JsonRun.Fail(CliDiagnostics.RecognizeSeveralPictures($"--image-kind {ImageKind.Name(forced)} reads one picture at a time"));
        if (o.Cell is not null) return JsonRun.Fail(CliDiagnostics.RecognizeBadValue("--cell", o.Cell, "a cell name, and only with a workspace path"));
        // A layout with a second path is still one layout at a time.
        if (DocumentKinds.Classify(o.Path!) != DocumentKind.Picture) { JsonRun.Report(CliDiagnostics.RecognizeMultiplePaths()); return Usage(); }
        var pictures = new List<(string Path, ImageSource Source, ImageKindResult Kind)>();
        foreach (string given in new[] { o.Path! }.Concat(o.Pictures))
        {
            if (!File.Exists(given)) return JsonRun.Fail(CliDiagnostics.RecognizePathNotFound(given));
            string path = Path.GetFullPath(given);
            if (DocumentKinds.Classify(path) != DocumentKind.Picture)
                return JsonRun.Fail(CliDiagnostics.RecognizeSeveralPictures($"'{given}' is not a picture"));
            var decoded = ImageSource.FromFile(path);
            if (decoded.Source is not { } source) return JsonRun.Fail(CliDiagnostics.PictureUnreadable(Verb, path, decoded.Refusal ?? "not a picture"));
            var read = ImageKind.Classify(source.Raster);
            var kind = read.Kind == DrawingKind.Plot ? read : read.Force(DrawingKind.Plot);
            if (read.Kind != DrawingKind.Plot)
                Console.Error.WriteLine($"[circuitRF] {given} reads as {(read.Kind == DrawingKind.None ? "no drawing" : $"a {read.Name} picture")}; "
                                        + "several pictures are read as plots");
            pictures.Add((path, source, kind));
        }
        return RecognisePlots(o, pictures);
    }

    /// <summary>The layout-only flags of the artwork recognition — a schematic picture has no copper to read them on.</summary>
    private static IEnumerable<string> LayoutOnly(Options o)
    {
        if (o.Bom is not null) yield return "--bom";
        if (o.Placement is not null) yield return "--placement";
        if (o.PlacementOrigin is not null) yield return "--placement-origin";
        if (o.PlacementUnit is not null) yield return "--placement-unit";
        if (o.Region is not null) yield return "--region";
        if (o.Ground is not null) yield return "--ground";
        if (o.GroundAt is not null) yield return "--ground-at";
        if (o.Vias is not null) yield return "--vias";
        if (o.Coplanar is not null) yield return "--coplanar";
        if (o.CoplanarFactor is not null) yield return "--coplanar-factor";
        if (o.FreeOrientation) yield return "--free-orientation";
    }

    /// <summary>The folder a new cell goes into: the picture's workspace, as the dialog's is — else the picture's own.</summary>
    private static string PictureParent(string picture) =>
        DocumentKinds.AncestorCws(picture) is { } cws ? Path.GetDirectoryName(cws)! : Path.GetDirectoryName(picture)!;

    /// <summary>R-im11-2 — a layout picture: <see cref="ImageRecognition"/>, which traces it (every R-im11-1 flag) and
    /// recognises the trace with every artwork flag.</summary>
    private static int RecogniseLayoutPicture(Options o, string path, ImageSource source, ImageKindResult kind, ImageReadJson image,
                                              Technology tech, string? techPath, RecognitionEmitOptions emit)
    {
        PixelRect? region = null;
        if (o.Region is { } r)
        {
            var (rect, refusal) = PictureFlags.Region(Verb, r);
            if (refusal is { } no) return no;
            region = rect;
        }
        var (traceInput, inputRefusal) = o.Picture.TraceInput(Verb, source, kind, tech, techPath, region);
        if (inputRefusal is { } noInput) return noInput;

        var options = new RecognitionOptions();
        if (o.GroundAt is { } at)
        {
            var (p, refusal) = Coordinates(at, "--ground-at", 2, traceInput!.DbuPerMicron);
            if (refusal is { } no) return no;
            options = options with { GroundAt = (p![0], p[1]) };
        }
        if (o.Ground is { } net)            options = options with { GroundNet = net };
        if (o.Vias is { } vias)             options = options with { Vias = vias };
        if (o.Coplanar is { } coplanar)     options = options with { Coplanar = coplanar };
        if (o.CoplanarFactor is { } factor) options = options with { CoplanarGapFactor = factor };
        if (o.Stop is { } top)              options = options with { TopFrequencyHz = top.Hz };
        var input = new ImageRecognitionInput
        {
            Trace = traceInput!, Options = options, PartsCsvPath = o.Parts is { } parts ? Path.GetFullPath(parts) : null,
        };
        if (o.Placement is { } placementPath)
        {
            if (!File.Exists(placementPath)
                || PlacementFile.ReadFile(placementPath, traceInput!.DbuPerMicron, o.PlacementOrigin, o.PlacementUnit) is not { } placement)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--placement", placementPath));
            input = input with { Placement = placement };
        }
        if (o.Bom is { } bomPath)
        {
            if (!File.Exists(bomPath) || RecognitionBom.ReadFile(bomPath) is not { } bom)
                return JsonRun.Fail(CliDiagnostics.RecognizeFileNotFound("--bom", bomPath));
            input = input with { Bom = bom };
        }

        RecognizeReportJson Picture(RecognizeReportJson j, ImageTraceResult trace) =>
            j with { Image = image, Scale = PictureFlags.ScaleJson(trace), Layers = PictureFlags.LayersJson(trace) };
        void Overlay(ImageTraceResult trace, RecognitionResult? recognition)
        {
            if (o.Picture.LayerMap is null && trace.LayerMap is { } map)
                Console.Error.WriteLine($"[circuitRF] layer map: --layer-map \"{PictureFlags.Spell(map)}\"");
            o.Picture.WriteOverlay(Verb, ImageOverlay.Build(trace, recognition, tech), traceInput!.Source);
        }

        // D6, headless: with no scale stated the trace is made only to say what evidence there is.
        if (o.Picture.Mode is PictureFlags.ScaleMode.Unstated or PictureFlags.ScaleMode.Auto)
        {
            var trace = ImageTrace.Trace(traceInput!, RunHost.Control);
            if (trace.Refusal is null && o.Picture.ScaleGate(Verb, trace) is { } noScale)
            {
                Overlay(trace, null);
                JsonRun.Recognize = Picture(Document(path, techPath, o.Region, 0, trace.Report, PartsTable.Empty), trace);
                return noScale;
            }
        }

        Console.Error.WriteLine($"[circuitRF] recognising {path}");
        ImageRecognitionCircuit reading;
        string? schematic = null, cell = null, refused;
        bool checkpoint = false;
        if (o.Into is { } into)
        {
            string name = into[4..], cellDir = Path.Combine(PictureParent(path), name);
            ImageRecognitionTarget target;
            if (Directory.Exists(cellDir) && ImageRecognitionTarget.IsReplaceable(cellDir))
            {
                if (!o.Replace) return JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(name));
                target = ImageRecognitionTarget.Replace(cellDir);
            }
            else target = ImageRecognitionTarget.NewCell(PictureParent(path), name);
            var run = ImageRecognition.Run(input, target, new ImageRecognitionRunOptions { Emit = emit }, RunHost.Control);
            (reading, refused, schematic, cell, checkpoint) = (run.Reading, run.Refusal, run.SchematicPath, run.CellDir, run.CheckpointTaken);
        }
        else
        {
            reading = ImageRecognition.Circuit(input, emit, RunHost.Control);
            refused = reading.Refusal;
        }

        Overlay(reading.Trace, reading.Recognition);
        if (refused is not null)
        {
            JsonRun.Recognize = Picture(Document(path, techPath, o.Region, 0, reading.Report, reading.Recognition?.Parts ?? PartsTable.Empty), reading.Trace);
            return JsonRun.Fail(CliDiagnostics.RecognizeRefused(refused));
        }
        var circuit = reading.Circuit!;
        return Conclude(o, path, techPath, reading.Report, reading.Recognition!.Parts, circuit.TestBench.Instances.Count,
                        dir => circuit.CnlText(dir), schematic, cell, checkpoint, j => Picture(j, reading.Trace));
    }

    /// <summary>R-im11-2 — a schematic picture: <see cref="SchematicImageRecognition"/>, its crossings, arrangement and
    /// connection corrections given as flags; the parts table's rows are pixels.</summary>
    private static int RecogniseSchematicPicture(Options o, string path, ImageSource source, ImageKindResult kind, ImageReadJson image,
                                                 Technology? tech, string? techPath, RecognitionEmitOptions emit)
    {
        var options = new SchematicImageOptions { Overrides = [.. o.Picture.Overrides] };
        if (o.Picture.Crossings is { } crossings) options = options with { Crossings = crossings };
        if (o.Picture.Threshold is { } k) options = options with { LineArt = options.LineArt with { K = k } };
        options = options with
        {
            Profile = new PictureProfileOptions { Pen = o.Picture.Pen, TextHeight = o.Picture.TextHeight, SymbolScale = o.Picture.SymbolScale },
        };
        var input = new SchematicImageInput
        {
            Source = source, Kind = kind, Options = options, Technology = tech, TechnologyPath = techPath,
            PartsCsvPath = o.Parts is { } parts ? Path.GetFullPath(parts) : null,
            Arrange = o.Picture.Arrange ?? SchematicArrange.AsDrawn, KeepUnderlay = o.Picture.Underlay ?? true,
            Reading = o.Picture.Profile,
        };

        Console.Error.WriteLine($"[circuitRF] recognising {path}");
        SchematicImageResult reading;
        string? schematic = null, cell = null, refused;
        bool checkpoint = false;
        if (o.Into is { } into)
        {
            string name = into[4..], cellDir = Path.Combine(PictureParent(path), name);
            SchematicImageTarget target;
            if (Directory.Exists(cellDir) && SchematicImageTarget.IsReplaceable(cellDir))
            {
                if (!o.Replace) return JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(name));
                target = SchematicImageTarget.Replace(cellDir);
            }
            else target = SchematicImageTarget.NewCell(PictureParent(path), name);
            var run = SchematicImageRecognition.Run(input, target, new SchematicImageRunOptions { Emit = emit }, RunHost.Control);
            (reading, refused, schematic, cell, checkpoint) = (run.Reading, run.Refusal, run.SchematicPath, run.CellDir, run.CheckpointTaken);
        }
        else
        {
            reading = SchematicImageRecognition.Circuit(input, emit, RunHost.Control);
            refused = reading.Refusal;
        }

        // A cancelled reading returns cancelled (R-imf9-6); the CLI's own answer to a cancel is still its exit 130.
        if (reading.Cancelled) throw new OperationCanceledException();
        o.Picture.WriteOverlay(Verb, ImageOverlay.BuildSchematic(reading), source);
        var (symbols, words, marks) = PictureFlags.SchematicJson(reading);
        var readings = PictureFlags.ReadingJson(reading);
        RecognizeReportJson Picture(RecognizeReportJson j) => j with { Image = image, Symbols = symbols, Words = words, Crossings = marks, Reading = readings };
        if (refused is not null)
        {
            JsonRun.Recognize = Picture(Document(path, techPath, null, 0, reading.Report, reading.Parts));
            return JsonRun.Fail(CliDiagnostics.RecognizeRefused(refused));
        }
        var circuit = reading.Circuit!;
        return Conclude(o, path, techPath, reading.Report, reading.Parts, circuit.TestBench.Instances.Count,
                        dir => circuit.CnlText(dir), schematic, cell, checkpoint, Picture);
    }

    /// <summary>Where <c>--into</c> writes. The rules are <see cref="RecognitionTarget"/>'s; what is here is only the
    /// one question the GUI asks and a build machine cannot be asked — replace? — answered by <c>--replace</c>.</summary>
    private static (RecognitionTarget? Target, int? Refusal) Target(Options o, string into, string clay)
    {
        if (into == "artwork")
        {
            if (RecognitionTarget.ArtworkCellOffered(clay)) return (RecognitionTarget.ArtworkCell, null);
            if (RecognitionTarget.CellOf(clay) is { } artworkCell)
            {
                if (!RecognitionTarget.IsReplaceable(artworkCell))
                    return (null, JsonRun.Fail(CliDiagnostics.RecognizeArtworkHasSchematic(Path.GetFileName(artworkCell))));
                return o.Replace
                    ? (RecognitionTarget.Replace(artworkCell), null)
                    : (null, JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(Path.GetFileName(artworkCell))));
            }
            return (RecognitionTarget.ArtworkCell, null);   // a loose layout: the run's own refusal says so
        }

        string name = into[4..];
        string cellDir = RecognitionTarget.NewCellDir(clay, name);
        if (!Directory.Exists(cellDir)) return (RecognitionTarget.NewCell(name), null);
        if (RecognitionTarget.IsReplaceable(cellDir))
            return o.Replace
                ? (RecognitionTarget.Replace(cellDir), null)
                : (null, JsonRun.Fail(CliDiagnostics.RecognizeReplaceRequired(name)));
        // A hand-drawn schematic, or a folder that is not a recognised cell: the run refuses it whatever the flags.
        return (o.Replace ? RecognitionTarget.Replace(cellDir) : RecognitionTarget.NewCell(name), null);
    }

    /// <summary>The S-parameter sweep: null (the run's own default — the EM setup's, else D15's) unless a flag
    /// changes it; a flag changes only its own field. <see cref="RecognitionSweep.Compose"/> is the rule, shared with
    /// the dialog's Frequency fields.</summary>
    private static FrequencySpec? Sweep(Options o, RecognitionInput input) =>
        RecognitionSweep.Compose(input.EmSetup, o.Start, o.Stop, o.Npts);

    /// <summary><paramref name="count"/> comma-separated lengths, each with its unit — <c>render --window</c>'s
    /// spelling and its bare-number refusal — in the layout's DBU.</summary>
    private static (long[]? Values, int? Refusal) Coordinates(string text, string option, int count, int dbuPerMicron)
    {
        string expected = count == 4 ? "x0,y0,x1,y1, each with a unit" : "x,y, each with a unit";
        var parts = text.Split(',');
        if (parts.Length != count) return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue(option, text, expected)));
        var values = new long[count];
        for (int i = 0; i < count; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0 || !char.IsLetter(p[^1]))
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeCoordinateNeedsUnit(option, p)));
            if (!LayoutUnits.TryParse(p, LayoutUnit.Um, dbuPerMicron, out values[i]))
                return (null, JsonRun.Fail(CliDiagnostics.RecognizeBadValue(option, text, expected)));
        }
        return (values, null);
    }

    private static int? Write(string path, Action write)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            write();
            Console.Error.WriteLine($"[circuitRF] Wrote {path}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputFailed(path, ex.Message));
        }
    }
}
