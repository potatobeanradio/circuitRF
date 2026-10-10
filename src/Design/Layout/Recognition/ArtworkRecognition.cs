// Create Schematic from Artwork — the recognition's entry point. brief-artsch-3-board-graph.md R-as3-1;
// overview D1-D3, D16; docs/design/artwork-to-schematic.md.
//
// artwork (.clay + .ctech [+ .cem] [+ placement/BOM])
//    ├─ AS-3  board graph   — ground, via classes, signal islands, ports, scope
//    ├─ AS-4  parts         — evidence → parts table
//    ├─ AS-5  lines         — trace chains → line elements
//    └─ AS-6  emit          — TestBench → NetlistSchematic.Build → .csch
//
// Pure and side-effect free: it reads what it is handed, posts nothing and writes nothing — the GUI
// command, the CLI verb and the MCP tool all call this one function (D2), and each says what the report
// holds in its own way. A recognition is refused only when there is no technology, no copper in scope,
// or no port (D16); everything else imperfect is a finding.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What a recognition reads.</summary>
public sealed record RecognitionInput
{
    /// <summary>The layout view.</summary>
    public required LayoutView View { get; init; }

    /// <summary>Its resolved technology, or null — which is a refusal.</summary>
    public required Technology? Technology { get; init; }

    /// <summary>The FLATTENED artwork — placed cells expanded, as a DRC run and the trace review read it.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>The <c>.clay</c> on disk, when there is one — placed parts' cells resolve against it.</summary>
    public string? ClayPath { get; init; }

    /// <summary>The resolved technology's file, for the report.</summary>
    public string? TechnologyPath { get; init; }

    /// <summary>The layout's EM setup, when it has one: its ports come first (R-as3-6).</summary>
    public EmSetup? EmSetup { get; init; }

    /// <summary>Where <see cref="EmSetup"/> was read from.</summary>
    public string? EmSetupPath { get; init; }

    /// <summary>A placement file, read with its refusals intact.</summary>
    public PlacementTable? Placement { get; init; }

    /// <summary>A bill of materials (AS-4 reads it).</summary>
    public BomTable? Bom { get; init; }

    /// <summary>An edited parts table, laid over the parts read from the board (AS-4 R-as4-8).</summary>
    public string? PartsCsvPath { get; init; }

    /// <summary>An edited parts table held in memory rather than on disk — the GUI dialog's table edits, carried as
    /// the CSV text they would be (R-as8-2). Laid over exactly as <see cref="PartsCsvPath"/> is; wins over it.</summary>
    public string? PartsCsvText { get; init; }

    /// <summary>Further sources of evidence about parts — AS-10's silkscreen (R-as4-1 (4)).</summary>
    public IReadOnlyList<IPartEvidenceSource> EvidenceSources { get; init; } = [];

    /// <summary>Which part of the artwork is read (R-as3-7).</summary>
    public RecognitionScope Scope { get; init; } = RecognitionScope.Whole;

    /// <summary>What the user said.</summary>
    public RecognitionOptions Options { get; init; } = new();

    /// <summary>Why the layout could not be read for recognition, or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>
    /// A layout read by <see cref="TraceImpedanceAnalysis.LoadLayout"/> — the walk the trace review and
    /// railRF already use, not a third one.
    /// </summary>
    public static RecognitionInput From(TraceImpedanceAnalysis.LayoutSource source) => new()
    {
        View = source.View, Technology = source.Technology, Shapes = source.Shapes,
        ClayPath = source.Path, TechnologyPath = source.TechnologyPath, Refusal = source.Refusal,
    };

    /// <summary>
    /// A <c>.clay</c> on disk, with its EM setup: <paramref name="cemPath"/> when given, else the first
    /// <c>.cem</c> in its workspace that analyses it (<see cref="EmSetupResolver.FindSetupsForLayout"/>).
    /// </summary>
    public static RecognitionInput FromFile(string clayPath, string? cemPath = null)
    {
        var input = From(TraceImpedanceAnalysis.LoadLayout(clayPath));
        if (cemPath is null && WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(input.ClayPath)) is { } cws)
            cemPath = EmSetupResolver.FindSetupsForLayout(cws, input.ClayPath!).FirstOrDefault();
        if (cemPath is null) return input;
        try { return input with { EmSetup = EmSetupPersistence.LoadFromFile(cemPath), EmSetupPath = Path.GetFullPath(cemPath) }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            return input;
        }
    }
}

/// <summary>What a recognition produced.</summary>
/// <param name="Board">The board graph; null only when the artwork could not be read at all.</param>
/// <param name="Report">Every guess and omission, with counts.</param>
/// <param name="Refusal">Why nothing can be generated, or null.</param>
public sealed record RecognitionResult(BoardGraph? Board, RecognitionReport Report, string? Refusal)
{
    /// <summary>True when the board graph can be built on.</summary>
    public bool Ok => Refusal is null && Board is not null;

    /// <summary>The parts table (AS-4), with an edited table laid over it; empty when there is no board.</summary>
    public PartsTable Parts { get; init; } = PartsTable.Empty;

    /// <summary>The line elements and the nodes between them (AS-5); empty when there is no board.</summary>
    public LineRecognitionResult Lines { get; init; } = LineRecognitionResult.Empty;

    /// <summary>The trace review the parts and the lines were read from — ONE run per recognition (R-as5-1).</summary>
    public TraceImpedanceReport? Review { get; init; }

    /// <summary>How many times the trace review ran for this recognition — the counter R-as5-9's gate holds at one.</summary>
    internal int ReviewRuns { get; init; }
}

/// <summary>The recognition, written once (D2).</summary>
public static partial class ArtworkRecognition
{
    /// <summary>Recognises <paramref name="input"/>'s artwork.</summary>
    public static RecognitionResult Recognize(RecognitionInput input, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var report = new RecognitionReport();
        var token = control?.Token ?? CancellationToken.None;

        if (input.Refusal is { } refused) return new RecognitionResult(null, report, refused);
        if (input.Technology is not { } tech)
            return new RecognitionResult(null, report,
                "The layout resolves no technology, so it has no stackup: recognition needs to know which layers " +
                "are copper and what a via joins. Give the layout a technology, or open it in a workspace that has one.");

        // A companion file handed in and refused is never quietly dropped: an unstated placement origin
        // is the user's to state (the CLI names the flag), never a guess (R-as4-1 (2)).
        if (input.Placement is { Refusal: { } placementRefusal })
            return new RecognitionResult(null, report, $"The placement file cannot be used: {placementRefusal}");
        if (input.Bom is { Refusal: { } bomRefusal })
            return new RecognitionResult(null, report, $"The bill of materials cannot be used: {bomRefusal}");

        var fmt = RailLengthFormat.For(input.View);
        int dbu = input.View.DbuPerMicron;
        var options = input.Options;

        // ── the whole board, partitioned once ────────────────────────────────────────────────────────
        control?.BeginStage("Reading copper");
        var widest = TraceImpedanceAnalysis.WidestTraceDbu(tech, input.Shapes, dbu);
        var wholePieces = CopperPieces.Build(input.Shapes, tech, format: fmt);
        if (!wholePieces.Any)
            return new RecognitionResult(null, report, "The artwork has no copper on any conductor of the stackup.");
        var whole = new BoardCopper(wholePieces, tech, input.Shapes, dbu, widest);
        token.ThrowIfCancellationRequested();

        // ── ground, from the WHOLE board whatever the scope (R-as3-7) ──────────────────────────────
        control?.BeginStage("Reading ground");
        if (BoardGround.Read(whole, options, fmt, out string? groundRefusal) is not { } ground)
            return new RecognitionResult(null, report, groundRefusal);
        report.Add(RecognitionFindingClass.GroundChosen, 1, BoardGround.Describe(ground, whole, fmt),
                   ground.Anchor is { } ga ? [ga] : []);
        if (wholePieces.Refusals.Count > 0)
            report.Add(RecognitionFindingClass.ConflictingNetNames, wholePieces.Refusals.Count, string.Join(" ", wholePieces.Refusals));
        if (wholePieces.HairlineJoins is var hairline and > 0)
            report.Add(RecognitionFindingClass.HairlineGapsJoined, hairline,
                $"{hairline} gap{(hairline == 1 ? "" : "s")} under {LayerRegions.HairlineGapDbu / 1000.0:0.###} µm between " +
                $"pieces of copper {(hairline == 1 ? "was" : "were")} read as joined — shapes that meet, rounded apart onto the file's coordinate " +
                "grid (a pad and the trace leaving it).");

        // ── the scope ──────────────────────────────────────────────────────────────────────────────
        Paths64? scope = input.Scope.IsWhole ? null : input.Scope.Paths();
        var board = whole;
        IReadOnlyList<LayoutShape> scopedShapes = input.Shapes;
        if (scope is not null)
        {
            var clipped = Clip(input.Shapes, scope, tech);
            scopedShapes = clipped;
            var pieces = CopperPieces.Build(clipped, tech, format: fmt);
            if (!pieces.Any) return new RecognitionResult(null, report, "There is no copper in the selected region.");
            board = new BoardCopper(pieces, tech, clipped, dbu, widest);
            ReportGroundCut(whole, ground, scope, report, fmt);
        }
        token.ThrowIfCancellationRequested();

        // ── each scoped piece read through the whole board's ground ─────────────────────────────────
        int n = board.Pieces.Count;
        var body = new bool[n];
        var pad = new bool[n];
        var groundNet = new bool[n];
        var pour = new bool[n];
        for (int p = 0; p < n; p++)
        {
            if (!board.IsConductor[p]) continue;
            int w = p;
            if (!ReferenceEquals(board, whole))
            {
                var (x, y) = board.ProbeOf(p);
                w = whole.Pieces.IndexAt(x, y, board.Pieces.LayerOfPiece(p));
            }
            if (w < 0) continue;
            body[p] = ground.Body[w];
            pad[p] = ground.PadCandidate[w];
            groundNet[p] = ground.Nets.Contains(whole.Pieces.NetOfPiece(w));
            pour[p] = !groundNet[p] && whole.IsWide(w);
        }

        // ── islands: non-ground copper joined by the vias between it ─────────────────────────────────
        control?.BeginStage("Classifying vias");
        var islandOf = Islands(board, body, out int islandCount);
        var allPad = Enumerable.Repeat(true, islandCount).ToArray();
        var lineShaped = new bool[islandCount];
        var separatePour = new bool[islandCount];
        var onGroundNet = new bool[islandCount];
        for (int p = 0; p < n; p++)
        {
            if (islandOf[p] is not (var i and >= 0)) continue;
            if (!pad[p]) allPad[i] = false;
            if (!board.IsPadShaped(p)) lineShaped[i] = true;
            if (pour[p]) separatePour[i] = true;
            if (groundNet[p]) onGroundNet[i] = true;
        }

        var vias = ViaClassification.Classify(board, body, islandOf, allPad, options, out var isPadGround);
        ReportVias(vias, isPadGround, options, report, fmt);
        token.ThrowIfCancellationRequested();

        // ── which islands are the circuit's nodes ────────────────────────────────────────────────────
        var placed = PlacedPadsOf(input, tech);
        var pinsOn = new List<PlacedPin>[islandCount];
        for (int i = 0; i < islandCount; i++) pinsOn[i] = [];
        var partSize = placed.Where(p => p.Refdes is { Length: > 0 })
                             .GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var signal = new bool[islandCount];
        foreach (var pin in placed)
        {
            int piece = board.ConductorAt(pin.X, pin.Y, pin.Layer);
            if (piece < 0 || islandOf[piece] is not (var i and >= 0)) continue;
            pinsOn[i].Add(pin);
            // A two-pad part's land makes its copper a node; a multi-pin part's does not (D9) — its pad
            // is a port only where the island is a node on its own account.
            if (pin.Refdes is { } r && partSize.GetValueOrDefault(r) <= 2) signal[i] = true;
        }
        var shorted = new bool[islandCount];
        foreach (var v in vias)
        {
            bool kept = v.Element != ViaElement.None || v.Class == ViaClass.PadGround;
            foreach (int i in v.Islands)
            {
                if (v.Class is ViaClass.PadGround or ViaClass.SignalShort) shorted[i] = true;
                if (kept) signal[i] = true;
            }
        }
        for (int i = 0; i < islandCount; i++)
            signal[i] = !isPadGround[i] && (signal[i] || lineShaped[i] || separatePour[i]);

        // ── ports ────────────────────────────────────────────────────────────────────────────────────
        control?.BeginStage("Finding ports");
        var portCtx = new PortContext(board, whole, body, islandOf, signal, separatePour, input.View, input.EmSetup,
                                      placed, input.Placement, scope, Outline(whole, input.Shapes, tech), options);
        var ports = PortDiscovery.Discover(portCtx, out var notOnSignal);
        foreach (var port in ports) signal[port.Island] = !isPadGround[port.Island];
        ReportPorts(ports, notOnSignal, report, fmt);
        token.ThrowIfCancellationRequested();

        // ── the islands, recorded ────────────────────────────────────────────────────────────────────
        var islands = new List<BoardIsland>(islandCount);
        var piecesOf = new List<int>[islandCount];
        for (int i = 0; i < islandCount; i++) piecesOf[i] = [];
        for (int p = 0; p < n; p++) if (islandOf[p] >= 0) piecesOf[islandOf[p]].Add(p);
        var viasOn = new List<int>[islandCount];
        for (int i = 0; i < islandCount; i++) viasOn[i] = [];
        for (int k = 0; k < vias.Count; k++) foreach (int i in vias[k].Islands) viasOn[i].Add(k);
        var rank = Conductors.Of(tech).SelectMany(c => c.DrawingLayers).Distinct().Select((l, r) => (l, r)).ToDictionary(t => t.l, t => t.r);

        for (int i = 0; i < islandCount; i++)
        {
            var box = Bbox.Empty;
            double area = 0;
            string? net = null;
            foreach (int p in piecesOf[i])
            {
                box = box.Union(board.Pieces.BoundsOfPiece(p));
                area += board.Area(p);
                net ??= board.Pieces.NameOfNet(board.Pieces.NetOfPiece(p));
            }
            var layers = piecesOf[i].Select(p => board.Pieces.LayerOfPiece(p)).Distinct()
                                    .OrderBy(l => rank.GetValueOrDefault(l, int.MaxValue)).ToList();
            var kind = isPadGround[i] ? IslandKind.PadGround : signal[i] ? IslandKind.Signal : IslandKind.Nothing;
            islands.Add(new BoardIsland(i, kind, layers, area, box, viasOn[i], pinsOn[i], net,
                                        shorted[i] || (onGroundNet[i] && !isPadGround[i]), separatePour[i])
                        { Pieces = piecesOf[i] });
        }
        var graph = new BoardGraph(ground, islands, vias, ports, dbu) { Copper = board.Pieces, IslandOfPiece = islandOf };

        // ── parts (AS-4): every evidence source, then the user's table laid over them ─────────────────
        control?.BeginStage("Reading parts");
        TraceImpedanceReport? review = null;
        int reviews = 0;
        TraceImpedanceReport Review()
        {
            if (review is null) { reviews++; review = ReviewOf(scopedShapes, tech, board, dbu, control); }
            return review;
        }
        var parts = PartReading.Read(new PartReadingContext
        {
            Input = input, Board = graph, Copper = board, Shapes = scopedShapes, Scope = scope, PlacedPads = placed,
            TraceRuns = () => Review().Layers.SelectMany(l => l.Traces).ToList(),
            Format = fmt,
        }, report);
        if (input.PartsCsvText is not null || input.PartsCsvPath is not null)
        {
            var edited = input.PartsCsvText is { } text ? PartsTableCsv.Read(text, parts) : PartsTableCsv.ReadFile(input.PartsCsvPath!, parts);
            if (edited.Refusal is { } why) return new RecognitionResult(graph, report, why);
            parts = edited.Table!;
            report.Add(RecognitionFindingClass.PartsCsvNotes, edited.Notes.Count, string.Join(" ", edited.Notes));
            report.Add(RecognitionFindingClass.PartsCsvRefdesNotOnBoard, edited.NotOnBoard.Count,
                $"The parts table names {Plural(edited.NotOnBoard.Count, "part", "parts")} the board does not have, and " +
                $"{(edited.NotOnBoard.Count == 1 ? "it was" : "they were")} ignored: {string.Join(", ", edited.NotOnBoard)}.");
        }

        // A part's pads make their copper a node: an island read as nothing that a modelled part lands on
        // is promoted (AS-3 kept it in the graph for exactly this).
        var promoted = parts.Rows.Where(r => r.IsModelled).SelectMany(r => r.Terminals).Select(t => t.Island)
                            .Where(i => i >= 0 && islands[i].Kind == IslandKind.Nothing).ToHashSet();
        if (promoted.Count > 0)
            graph = graph with { Islands = [.. islands.Select(i => promoted.Contains(i.Id) ? i with { Kind = IslandKind.Signal } : i)] };
        ReportIslands([.. graph.Islands], report, fmt);
        token.ThrowIfCancellationRequested();

        // ── lines (AS-5): the same review, read into line elements ───────────────────────────────────
        control?.BeginStage("Reading lines");
        var lines = LineRecognition.Recognize(new LineRecognitionContext
        {
            Board = graph, Parts = parts, Review = Review(), Technology = tech, Options = options,
            TopFrequencyHz = TopFrequency(input), Format = fmt,
        }, report);

        return new RecognitionResult(graph, report, ports.Count == 0 ? PortDiscovery.NoPortRefusal : null)
        {
            Parts = parts, Lines = lines, Review = review, ReviewRuns = reviews,
        };
    }

    /// <summary>
    /// The trace review recognition reads (R-as5-1): every trace in scope, the SHORT ones too — a region
    /// round the whole of the copper selects every chain, and a chain a selector chooses is a trace from
    /// <see cref="TraceImpedanceAnalysis.SelectedMinAspect"/> widths, so a 2–4-width line between two parts is a
    /// line and not a pad. Its target and tolerance do not matter here and its findings are not read.
    /// </summary>
    private static TraceImpedanceReport ReviewOf(IReadOnlyList<LayoutShape> shapes, Technology tech, BoardCopper board,
                                                 int dbu, RunControl? control)
    {
        var box = board.CopperBounds();
        long m = 1000L * dbu;
        long x0 = box.MinX - m, y0 = box.MinY - m, x1 = box.MaxX + m, y1 = box.MaxY + m;
        var everything = new TraceImpedanceScope { Regions = [new TraceScopeRegion(null, [x0, y0, x1, y0, x1, y1, x0, y1])] };
        return TraceImpedanceAnalysis.Analyze(shapes, tech, dbu, new TraceImpedanceOptions { Scope = everything }, control);
    }

    /// <summary>D15's top frequency: the option, else the EM setup's stop where it is a plain number with a
    /// unit, else 6 GHz.</summary>
    private static double TopFrequency(RecognitionInput input)
    {
        if (input.Options.TopFrequencyHz is { } f && f > 0 && double.IsFinite(f)) return f;
        if (input.EmSetup?.Frequency is { } spec
            && double.TryParse(spec.StopExpr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double stop))
        {
            double scale = spec.StopUnit.Trim().ToLowerInvariant() switch
            {
                "thz" => 1e12, "ghz" => 1e9, "mhz" => 1e6, "khz" => 1e3, "hz" => 1, _ => double.NaN,
            };
            if (stop * scale is var hz && hz > 0 && double.IsFinite(hz)) return hz;
        }
        return RecognitionOptions.DefaultTopFrequencyHz;
    }

    // ── islands ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Union-find over the non-ground conductor pieces, joined through every barrel that
    /// meets two of them. Islands are numbered in order of their first piece.</summary>
    private static int[] Islands(BoardCopper board, bool[] body, out int count)
    {
        int n = board.Pieces.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        foreach (var v in board.Vias)
        {
            int first = -1;
            foreach (int c in v.Touched)
            {
                if (body[c] || !board.IsConductor[c]) continue;
                if (first < 0) first = c;
                else parent[Find(c)] = Find(first);
            }
        }

        var islandOf = new int[n];
        var idOfRoot = new Dictionary<int, int>();
        count = 0;
        for (int p = 0; p < n; p++)
        {
            if (!board.IsConductor[p] || body[p]) { islandOf[p] = -1; continue; }
            int root = Find(p);
            if (!idOfRoot.TryGetValue(root, out int id)) idOfRoot[root] = id = count++;
            islandOf[p] = id;
        }
        return islandOf;
    }

    /// <summary>Every placed part's pads, with the layer each lands on — railRF's and LVS's own walk.</summary>
    private static IReadOnlyList<PlacedPin> PlacedPadsOf(RecognitionInput input, Technology tech)
    {
        if (input.View.Instances.Count == 0 || input.ClayPath is null) return [];
        var origins = new List<PlacedPinOrigin>();
        var pads = PlacedPins.Of(input.View, input.ClayPath, tech, PinNaming.ArtworkOnly,
                                 origins: origins, scope: PlacementScope.Designated);
        var result = new List<PlacedPin>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
            result.Add(i < origins.Count ? pads[i] with { Layer = origins[i].Layer } : pads[i]);
        return result;
    }

    // ── the scope ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The artwork inside <paramref name="scope"/>: copper clipped with <see cref="LayoutClipper"/>,
    /// a via or a label kept where its centre is inside.</summary>
    private static List<LayoutShape> Clip(IReadOnlyList<LayoutShape> shapes, Paths64 scope, Technology tech)
    {
        var bounds = DrcRegions.BoundsOf(scope);
        var clipped = new List<LayoutShape>();
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case BitmapShape: continue;
                case ViaShape v: if (Regions.Contains(scope, v.X, v.Y)) clipped.Add(v); continue;
                case LabelShape l: if (Regions.Contains(scope, l.X, l.Y)) clipped.Add(l); continue;
            }
            var paths = LayoutClipper.ToClipperPaths(shape, LayoutFlattener.ResolveTolDbu(shape, tech));
            if (paths.Count == 0 || !DrcRegions.BoundsOf(paths).Intersects(bounds)) continue;
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Intersection, paths, scope, tree, LayoutClipper.Rule);
            clipped.AddRange(LayoutClipper.FromClipperTree(tree, shape.Layer, shape.Net));
        }
        return clipped;
    }

    /// <summary>R-as3-7: the report says when the scope cut a ground pour.</summary>
    private static void ReportGroundCut(BoardCopper whole, GroundChoice ground, Paths64 scope, RecognitionReport report, RailLengthFormat fmt)
    {
        var anchors = new List<RecognitionAnchor>();
        for (int p = 0; p < whole.Pieces.Count; p++)
        {
            if (!ground.Body[p]) continue;
            var paths = whole.Pieces.PathsOfPiece(p);
            if (Clipper.BooleanOp(ClipType.Intersection, paths, scope, LayoutClipper.Rule).Count == 0) continue;
            if (Clipper.BooleanOp(ClipType.Difference, paths, scope, LayoutClipper.Rule).Count == 0) continue;
            var (x, y) = whole.ProbeOf(p);
            anchors.Add(new RecognitionAnchor(x, y, whole.Pieces.LayerOfPiece(p)));
        }
        report.Add(RecognitionFindingClass.ScopeCutGround, anchors.Count,
            $"The selection cuts {Plural(anchors.Count, "ground pour or plane", "ground pours and planes")}; ground was read " +
            "from the whole board, so the copper outside the selection is still the same ground.", anchors);
    }

    /// <summary>The board outline: the importer's outline layer where it drew one, else the copper's extent.</summary>
    private static Bbox Outline(BoardCopper whole, IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        var outlineLayers = tech.Layers.Where(l => string.Equals(l.Name, "Outline", StringComparison.OrdinalIgnoreCase))
                                       .Select(l => l.Key).ToHashSet();
        var box = Bbox.Empty;
        if (outlineLayers.Count > 0)
            foreach (var s in shapes)
                if (outlineLayers.Contains(s.Layer) && s is not (LabelShape or ViaShape or BitmapShape))
                    box = box.Union(DrcRegions.BoundsOf(LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, tech))));
        return box.IsEmpty ? whole.CopperBounds() : box;
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    private static void ReportVias(List<RecognizedVia> vias, bool[] isPadGround, RecognitionOptions options,
                                   RecognitionReport report, RailLengthFormat fmt)
    {
        static RecognitionAnchor A(RecognizedVia v) => new(v.X, v.Y, v.Layer);
        var stitching = vias.Where(v => v.Class == ViaClass.Stitching).ToList();
        report.Add(RecognitionFindingClass.StitchingViasDropped, stitching.Count,
            $"{Plural(stitching.Count, "via joins", "vias join")} ground to ground (stitching, fences, plane ties) and " +
            $"{(stitching.Count == 1 ? "was" : "were")} dropped.", [.. stitching.Select(A)]);

        var toGround = vias.Where(v => v.Element is ViaElement.ViaGnd or ViaElement.Gnd).ToList();
        string asWhat = options.Vias == ViaPolicy.Ground ? "plain grounds (vias=ground)" : "VIAGND";
        report.Add(RecognitionFindingClass.GroundViasKept, toGround.Count,
            $"{Plural(toGround.Count, "via", "vias")} to ground — a ground pad's or a shorted line's — kept as {asWhat}.",
            [.. toGround.Select(A)]);

        var capped = vias.Where(v => v.Class == ViaClass.PadGround && v.Element == ViaElement.None).ToList();
        report.Add(RecognitionFindingClass.GroundViasCapped, capped.Count,
            $"{Plural(capped.Count, "more via", "more vias")} on ground pads past the nearest {options.MaxGroundViasPerPad} " +
            "per pad, counted and not modelled.", [.. capped.Select(A)]);

        if (options.Vias == ViaPolicy.Model)
        {
            var coupled = vias.Where(v => v.Element == ViaElement.ViaGnd && v.Class == ViaClass.PadGround)
                              .GroupBy(v => v.Islands.FirstOrDefault(i => isPadGround[i]))
                              .Where(g => g.Count() > 1).ToList();
            if (coupled.Count > 0)
                report.Add(RecognitionFindingClass.GroundViaCouplingNotModelled, coupled.Count,
                    $"{Plural(coupled.Count, "ground pad has", "ground pads have")} more than one VIAGND; the mutual " +
                    "inductance between neighbouring vias is not modelled, so their combined inductance reads a little low.",
                    [.. coupled.Select(g => A(g.First()))]);
        }

        var signal = vias.Where(v => v.Class == ViaClass.SignalTransition).ToList();
        report.Add(RecognitionFindingClass.SignalViasKept, signal.Count,
            $"{Plural(signal.Count, "via carries", "vias carry")} a signal between layers, kept as VIA.", [.. signal.Select(A)]);

        var nothing = vias.Where(v => v.Class == ViaClass.JoinsNothing).ToList();
        report.Add(RecognitionFindingClass.ViasJoiningNothing, nothing.Count,
            $"{Plural(nothing.Count, "via meets", "vias meet")} one conductor or none and {(nothing.Count == 1 ? "joins" : "join")} nothing.",
            [.. nothing.Select(A)]);
    }

    private static void ReportPorts(List<RecognizedPort> ports, List<RecognitionAnchor> notOnSignal,
                                    RecognitionReport report, RailLengthFormat fmt)
    {
        foreach (var (source, cls, what) in new (PortSource, RecognitionFindingClass, string)[]
                 {
                     (PortSource.EmSetup, RecognitionFindingClass.PortsFromEmSetup, "from the layout's EM setup"),
                     (PortSource.PortLabel, RecognitionFindingClass.PortsFromLabels, "from port labels"),
                     (PortSource.LayoutPin, RecognitionFindingClass.PortsFromPins, "from layout pins"),
                     (PortSource.MultiPinPart, RecognitionFindingClass.PortsAtMultiPinParts, "at the pads of parts with more than two pads, which are cut out"),
                     (PortSource.BoardEdge, RecognitionFindingClass.PortsAtBoardEdge, "where a line reaches the board edge"),
                     (PortSource.Connector, RecognitionFindingClass.PortsAtConnectors, "at connector footprints"),
                     (PortSource.ScopeCut, RecognitionFindingClass.PortsAtScopeCut, "where the selection's boundary cuts a line"),
                 })
        {
            var these = ports.Where(p => p.Source == source).ToList();
            report.Add(cls, these.Count, $"{Plural(these.Count, "port", "ports")} {what}: {string.Join(", ", these.Select(p => p.Name))}.",
                       [.. these.Select(p => new RecognitionAnchor(p.X, p.Y, p.Layer))]);
        }
        report.Add(RecognitionFindingClass.PortsNotOnSignalCopper, notOnSignal.Count,
            $"{Plural(notOnSignal.Count, "port label, pin or connector lands", "port labels, pins or connectors land")} on no signal copper " +
            $"and {(notOnSignal.Count == 1 ? "is" : "are")} no port: {string.Join(", ", notOnSignal.Take(6).Select(a => fmt.Point(a.X, a.Y)))}" +
            $"{(notOnSignal.Count > 6 ? ", …" : "")}.", notOnSignal);
    }

    private static void ReportIslands(List<BoardIsland> islands, RecognitionReport report, RailLengthFormat fmt)
    {
        static RecognitionAnchor Centre(BoardIsland i) =>
            new((i.Bounds.MinX + i.Bounds.MaxX) / 2, (i.Bounds.MinY + i.Bounds.MaxY) / 2, i.Layers.FirstOrDefault());

        var pours = islands.Where(i => i.IsSeparatePour).ToList();
        report.Add(RecognitionFindingClass.SeparatePour, pours.Count,
            $"{Plural(pours.Count, "pour is", "pours are")} galvanically separate from ground and read as signal: " +
            string.Join("; ", pours.Select(p => $"{(p.NetName is { } nm ? $"'{nm}'" : "unnamed")} at {fmt.Point(Centre(p).X, Centre(p).Y)}")) + ".",
            [.. pours.Select(Centre)]);

        var nothing = islands.Where(i => i.Kind == IslandKind.Nothing).ToList();
        report.Add(RecognitionFindingClass.CopperReadAsNothing, nothing.Count,
            $"{Plural(nothing.Count, "piece of copper touches", "pieces of copper touch")} no part, port or line " +
            "(a test point, a logo, a fiducial) and " + (nothing.Count == 1 ? "was" : "were") + " left out.",
            [.. nothing.Select(Centre)]);
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
