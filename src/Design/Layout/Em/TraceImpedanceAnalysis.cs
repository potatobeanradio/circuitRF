// Trace Impedance Analysis — every trace on the chosen layers, end to end, against a target Z0 and a
// tolerance, with where the return path under each one breaks (round-8 field report; the layout
// review a board designer carries out before sending artwork to a fabricator with an impedance-control
// note). The probe (TraceImpedanceProbe) answers "what is THIS trace" at one click; this answers it for
// the whole board, station by station, from the same cross-section solve.
//
// ── FINDING TRACES IN COPPER THAT HAS NO TRACES ────────────────────────────────────────────────
//
// Imported artwork is unioned polygons: a trace is not an object, it is a stretch of copper with two
// long parallel edges facing each other. So that is what is looked for.
//
// 1. PIECES. Every pair of long edges on one layer that are anti-parallel (within 2°), face each other
//    across copper (each on the other's copper side, and nothing in between — a ray from one hits the
//    other first), are at most MaxWidth apart and overlap along their length, bounds a strip: a PIECE,
//    with a centre line, a width and a direction. Pieces tile a trace: where either edge ends (a
//    vertex, a jog, a bend, a pad) one piece ends and the next begins.
// 2. POURS. A copper island whose area is many times the area of the pieces found in it is a pour or a
//    plane, and its pieces are the slivers between its antipads — not traces. So is an island that
//    carries a row of vias. Those are skipped, and the report says how many.
// 3. CHAINS. Piece ends within about one width of each other, on the same island, are joined —
//    straight through a jog, round a bend or a mitre, across a width step. An end with TWO such
//    neighbours is a junction, and every trace meeting there ends there. The corner region of a bend
//    is not cut (a hard angle has no single width, the probe's own rule) and is not flagged. A via
//    ends a trace: what continues on another layer is that layer's trace.
// 4. STATIONS. Each piece is cut every half width along its centre line. Every cut is the probe's
//    own cut (TraceCrossSection) — the reference found by COPPER COVERAGE, every other conductor in
//    reach held at ground — and cuts with the same geometry share one solve, which is what makes a
//    whole board affordable: on a real board almost every station along a trace is a repeat.
// 5. FINDINGS, per trace: where Z0 leaves target ± tolerance; where the nearest layer below (or
//    above) stops covering the trace while covering it elsewhere along the same trace (a broken
//    return); where the reference itself steps to another layer; and what each end is.

using System.Diagnostics;
using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What <see cref="TraceImpedanceAnalysis.Analyze"/> is asked for.</summary>
public sealed record TraceImpedanceOptions
{
    /// <summary>The characteristic impedance every trace is held to.</summary>
    public double TargetOhms { get; init; } = DefaultTargetOhms;

    /// <summary>± this many percent of <see cref="TargetOhms"/> passes.</summary>
    public double TolerancePercent { get; init; } = DefaultTolerancePercent;

    /// <summary>The copper layers to analyse; null for every drawing layer bound to a conductor of the
    /// stackup that carries copper.</summary>
    public IReadOnlyList<LayerKey>? Layers { get; init; }

    /// <summary>The widest copper read as a trace; null derives it per layer from the stackup
    /// (ten times the distance to the nearest other conductor, between 1 and 8 mm).</summary>
    public double? MaxWidthMicrons { get; init; }

    /// <summary>How lengths and coordinates read in the report. Null — the default — is the
    /// LAYOUT's own display unit (the <c>.clay</c>'s <c>DisplayUnit</c>), which is what a report
    /// about that layout must speak (owner, 2026-09-25); µm where there is no layout to ask.</summary>
    public LayoutUnit? DisplayUnit { get; init; }

    public const double DefaultTargetOhms = 50;
    public const double DefaultTolerancePercent = 10;
}

/// <summary>What kind of finding a <see cref="TraceIssue"/> is.</summary>
public enum TraceIssueKind
{
    /// <summary>Z0 is outside target ± tolerance over this stretch.</summary>
    OutOfTolerance,
    /// <summary>The nearest layer below or above stops covering the trace here, and covers it
    /// elsewhere along the same trace: the return path is broken.</summary>
    ReturnBroken,
    /// <summary>The nearest layer covers only part of the trace's width here.</summary>
    PartialReference,
    /// <summary>The reference steps from one layer to another here.</summary>
    ReferenceStep,
    /// <summary>No copper covers the trace on either side here.</summary>
    NoReference,
    /// <summary>The cross-section could not be solved here.</summary>
    Unsolved,
}

/// <summary>One finding on one trace, at a stretch of it. Coordinates in DBU.</summary>
public sealed record TraceIssue(TraceIssueKind Kind, long X0, long Y0, long X1, long Y1, string Text)
{
    public long X => (X0 + X1) / 2;
    public long Y => (Y0 + Y1) / 2;

    /// <summary>Whether this finding fails the trace. Every kind does; the property is here so a
    /// reader does not have to know that.</summary>
    public bool Fails => true;
}

/// <summary>One cut along a trace. Coordinates and lengths in DBU.</summary>
public sealed record TraceStation
{
    public long X { get; init; }
    public long Y { get; init; }

    /// <summary>The cut direction (across the trace), unit.</summary>
    public double Ux { get; init; }
    public double Uy { get; init; }

    /// <summary>Distance along the trace from its start to this cut.</summary>
    public double S { get; init; }

    /// <summary>The length of trace this cut stands for.</summary>
    public double Length { get; init; }

    public double Width { get; init; }
    public double? Z0 { get; init; }
    public double? Eeff { get; init; }
    public string? Configuration { get; init; }
    public string? ReferenceBelow { get; init; }
    public string? ReferenceAbove { get; init; }
    public double? GapLeft { get; init; }
    public double? GapRight { get; init; }

    /// <summary>Why there is no Z0 here, or null.</summary>
    public string? Refusal { get; init; }
}

public enum TraceVerdict { Pass, Fail, Unsolved }

/// <summary>One trace, end to end on one layer.</summary>
public sealed record TraceRun
{
    /// <summary>"T1", "T2", … — numbered across the whole report, so a row and a label on a page
    /// name the same trace.</summary>
    public string Id { get; init; } = "";

    public LayerKey Layer { get; init; }
    public string LayerName { get; init; } = "";

    /// <summary>The cuts, in order from <see cref="StartX"/>, <see cref="StartY"/>.</summary>
    public IReadOnlyList<TraceStation> Stations { get; init; } = [];

    /// <summary>The centre line as drawn by the pieces, start to end: pairs of points, each pair
    /// one straight piece, with the gaps between pairs being bends, jogs or steps. DBU.</summary>
    public IReadOnlyList<(long X0, long Y0, long X1, long Y1, double Width)> Pieces { get; init; } = [];

    public long StartX { get; init; }
    public long StartY { get; init; }
    public long EndX { get; init; }
    public long EndY { get; init; }

    /// <summary>What each end is: "via", "pad", "junction", "open end" or "continues".</summary>
    public string StartsAt { get; init; } = "";
    public string EndsAt { get; init; } = "";

    public double Length { get; init; }
    public double WidthMin { get; init; }
    public double WidthMax { get; init; }

    public double? Z0Min { get; init; }
    public double? Z0Max { get; init; }

    /// <summary>Length-weighted over the solved stations.</summary>
    public double? Z0Mean { get; init; }

    /// <summary>The fraction of the solved length inside target ± tolerance, 0–1.</summary>
    public double InTolerance { get; init; }

    /// <summary>The line type over most of the trace — "grounded coplanar waveguide", "microstrip",
    /// "stripline"… (<see cref="TraceStation.Configuration"/>'s own names).</summary>
    public string Configuration { get; init; } = "";

    /// <summary>Every line type the trace is along some of its length, with its share of the solved
    /// length (0–1), most first. A trace is often BOTH: grounded CPW where the side ground runs beside
    /// it and microstrip where the ground falls away.</summary>
    public IReadOnlyList<(string Name, double Share)> Configurations { get; init; } = [];

    /// <summary>The line types in short form with their shares — "GCPW 80%, Microstrip 20%" — or the one
    /// type alone when there is only one.</summary>
    public string TypeSummary => Configurations.Count switch
    {
        0 => "—",
        1 => TraceImpedanceReport.ShortType(Configurations[0].Name),
        _ => string.Join(", ", Configurations.Select(c => $"{TraceImpedanceReport.ShortType(c.Name)} {c.Share * 100:0}%")),
    };

    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<TraceIssue> Issues { get; init; } = [];

    /// <summary>Things said about the trace that are not faults — a layer cleared under the whole
    /// length, for instance.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public TraceVerdict Verdict { get; init; }
}

/// <summary>One analysed layer.</summary>
public sealed record TraceLayerResult
{
    public LayerKey Layer { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<TraceRun> Traces { get; init; } = [];

    /// <summary>The layer's unioned copper, as flat x,y rings in DBU — what the page draws under the
    /// map.</summary>
    public IReadOnlyList<long[]> Copper { get; init; } = [];

    /// <summary>Copper islands read as pours or planes and not analysed.</summary>
    public int PoursSkipped { get; init; }

    /// <summary>The widest copper read as a trace on this layer, DBU.</summary>
    public double MaxWidth { get; init; }
}

/// <summary>The whole analysis.</summary>
public sealed record TraceImpedanceReport
{
    /// <summary>Why nothing was analysed, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public string Title { get; init; } = "";
    public string? SourcePath { get; init; }
    public string TechnologyName { get; init; } = "";

    /// <summary>The technology the layout resolved, for the report's stackup drawing.</summary>
    public Technology? Technology { get; init; }

    /// <summary>The <c>.ctech</c> it was read from, or null for one that is not a file.</summary>
    public string? TechnologyPath { get; init; }
    public double TargetOhms { get; init; }
    public double TolerancePercent { get; init; }
    public double LowOhms => TargetOhms * (1 - TolerancePercent / 100);
    public double HighOhms => TargetOhms * (1 + TolerancePercent / 100);

    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit DisplayUnit { get; init; } = LayoutUnit.Um;

    public IReadOnlyList<TraceLayerResult> Layers { get; init; } = [];

    /// <summary>Every layer the run set out to analyse, by name — more than <see cref="Layers"/>
    /// when it was cancelled.</summary>
    public IReadOnlyList<string> LayersRequested { get; init; } = [];

    /// <summary>The run was cancelled; <see cref="Layers"/> holds the layers it finished.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The whole artwork's extent, DBU — every layer page is framed on it so the pages line up.</summary>
    public Bbox Extent { get; init; } = Bbox.Empty;

    public IReadOnlyList<string> Notes { get; init; } = [];

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public int StationCount { get; init; }
    public int SolveCount { get; init; }
    public TimeSpan Elapsed { get; init; }

    public IEnumerable<TraceRun> AllTraces => Layers.SelectMany(l => l.Traces);
    public int TraceCount => Layers.Sum(l => l.Traces.Count);
    public int PassCount => AllTraces.Count(t => t.Verdict == TraceVerdict.Pass);
    public int FailCount => AllTraces.Count(t => t.Verdict == TraceVerdict.Fail);

    /// <summary>The decimals a number needs in the report's unit to say what
    /// <paramref name="umDecimals"/> decimals say in µm — 0.1 µm is three more places in mm.</summary>
    public int Decimals(int umDecimals) => Math.Max(0, umDecimals + DisplayUnit switch
    {
        LayoutUnit.Nm   => -3,
        LayoutUnit.Mm   => 3,
        LayoutUnit.Mil  => 1,
        LayoutUnit.Inch => 4,
        _               => 0,
    });

    /// <summary>A length in the report's unit, bare.</summary>
    public string Num(double dbu, int umDecimals = 1) =>
        LayoutUnits.Format((long)Math.Round(dbu), DisplayUnit, DbuPerMicron, Decimals(umDecimals));

    /// <summary>The report's unit suffix — the layout's own.</summary>
    public string Unit => LayoutUnits.Suffix(DisplayUnit);

    /// <summary>A length in the report's unit, with its suffix.</summary>
    public string Len(double dbu) => $"{Num(dbu)} {Unit}";

    /// <summary>A point in the report's unit, with its suffix.</summary>
    public string Pt(long x, long y) => $"({Num(x)}, {Num(y)}) {Unit}";

    internal static TraceImpedanceReport Refused(string why) => new() { Refusal = why };

    /// <summary>The short name a table column holds for a line type.</summary>
    public static string ShortType(string configuration) => configuration switch
    {
        "microstrip"                                    => "Microstrip",
        "grounded coplanar waveguide"                   => "GCPW",
        "microstrip with coplanar ground on one side"   => "GCPW 1-side",
        "stripline"                                     => "Stripline",
        "stripline with coplanar ground"                => "Stripline + CPW",
        "microstrip (reference above)"                  => "Microstrip (ref above)",
        "grounded coplanar waveguide (reference above)" => "GCPW (ref above)",
        "coplanar waveguide (no ground plane)"          => "CPW",
        _                                               => configuration,
    };

    /// <summary>What the short names stand for, for a legend.</summary>
    public static readonly IReadOnlyList<(string Short, string Long)> TypeLegend =
    [
        ("Microstrip", "a reference plane below, nothing beside"),
        ("GCPW", "grounded coplanar waveguide: a plane below and ground on the same layer both sides"),
        ("GCPW 1-side", "a plane below and ground on the same layer on one side only"),
        ("Stripline", "a reference plane below and above"),
        ("CPW", "ground on the same layer only, no plane"),
    ];
}

public static class TraceImpedanceAnalysis
{
    /// <summary>Two edges are parallel within this.</summary>
    public const double ParallelToleranceDeg = 2.0;

    /// <summary>An island is a pour when its area is more than this many times its pieces' area.</summary>
    public const double PourAreaRatio = 8;

    /// <summary>An island carrying at least this many vias is a pour, a plane or a ground strip.</summary>
    public const int PourViaCount = 4;

    /// <summary>A chain shorter than this many of its widths is a pad, not a trace.</summary>
    public const double MinAspect = 4;

    /// <summary>Cuts per width along a piece.</summary>
    public const double StationsPerWidth = 1;

    /// <summary>At most this many cuts on one piece.</summary>
    public const int MaxStationsPerPiece = 400;

    /// <summary>
    /// The analysis of a layout file on disk — read, its technology resolved as the editor resolves
    /// it, its placed cells flattened as a DRC run flattens them. What <c>circuitrf impedance</c> calls.
    /// </summary>
    public static TraceImpedanceReport AnalyzeFile(
        string clayPath, TraceImpedanceOptions options, RunControl? control = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clayPath);
        string full = Path.GetFullPath(clayPath);
        var view = LayoutPersistence.LoadFromFile(full);
        var cache = new TechnologyCache();
        var (resolved, _) = TechnologyResolver.ResolveForDocument(view.TechRef, full, null, cache);
        if (resolved.Tech is not { } tech)
            return TraceImpedanceReport.Refused(
                $"'{Path.GetFileName(full)}' resolves no technology, so it has no stackup: an impedance needs " +
                "the heights and the dielectric. Give the layout a technology, or open it in a workspace that has one.");

        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(full) ?? "") ?? "";
        var flat = LayoutDesignFlatten.Flatten(
            view, cellDir, tech,
            (techRef, subLayoutDir) => TechnologyResolver.ResolveForDocument(
                techRef, Path.Combine(subLayoutDir, "x.clay"), null, cache).Resolution,
            resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? view.Shapes : flat.Shapes;

        var report = Analyze(shapes, tech, view.DbuPerMicron,
                             options with { DisplayUnit = options.DisplayUnit ?? view.DisplayUnit }, control);
        return report with
        {
            Title = CellTitle(full),
            SourcePath = full,
            TechnologyPath = resolved.ResolvedPath,
        };
    }

    /// <summary>The cell's name for a layout inside a cell folder, the file's name otherwise.</summary>
    public static string CellTitle(string clayPath)
    {
        string? layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath));
        string? cellDir = layoutDir is null ? null : Path.GetDirectoryName(layoutDir);
        if (layoutDir is not null && Path.GetFileName(layoutDir).Equals("layout", StringComparison.OrdinalIgnoreCase)
            && cellDir is not null)
            return Path.GetFileName(cellDir);
        return Path.GetFileNameWithoutExtension(clayPath);
    }

    /// <summary>
    /// Every trace on the chosen layers of <paramref name="shapes"/>, analysed. <b>The shapes are the
    /// FLATTENED artwork</b>, as the probe takes them.
    /// </summary>
    public static TraceImpedanceReport Analyze(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        TraceImpedanceOptions options, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(options);
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        if (!(options.TargetOhms > 0))
            return TraceImpedanceReport.Refused("The target impedance must be a positive number of ohms.");
        if (!(options.TolerancePercent > 0) || options.TolerancePercent >= 100)
            return TraceImpedanceReport.Refused("The tolerance must be more than 0 % and less than 100 %.");

        var clock = Stopwatch.StartNew();
        var ct = control?.Token ?? CancellationToken.None;

        var (stack, bands, bandOf, stackRefusal) = TraceStack.StackOf(tech);
        if (stackRefusal is not null) return TraceImpedanceReport.Refused(stackRefusal);
        if (bands.Count == 0)
            return TraceImpedanceReport.Refused(
                $"The technology '{tech.Name}' has no conductor in its stackup, so there is no copper to analyse. " +
                "Add the stackup on the technology's Stackup tab.");

        string LayerName(LayerKey k) => tech.Layers.FirstOrDefault(l => l.Key == k)?.Name is { Length: > 0 } n
            ? n : $"layer {k.Layer}/{k.Datatype}";

        // ── the copper, per band, for the whole artwork ─────────────────────────────────────────
        control?.BeginStage("Reading copper");
        var subjects = new Dictionary<int, Paths64>();
        var vias = new Dictionary<int, List<(double X, double Y, double R)>>();
        var extent = Bbox.Empty;
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape && !bandOf.ContainsKey(shape.Layer)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (lk, _, paths) =>
            {
                if (!bandOf.TryGetValue(lk, out var band)) return;
                if (!subjects.TryGetValue(band.Index, out var list)) subjects[band.Index] = list = [];
                list.AddRange(paths);
                foreach (var p in paths)
                    foreach (var pt in p) extent = extent.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));
                if (shape is ViaShape v)
                {
                    if (!vias.TryGetValue(band.Index, out var vl)) vias[band.Index] = vl = [];
                    double r = 0;
                    foreach (var p in paths)
                        foreach (var pt in p) r = Math.Max(r, Math.Sqrt((double)(pt.X - v.X) * (pt.X - v.X) + (double)(pt.Y - v.Y) * (pt.Y - v.Y)));
                    vl.Add((v.X, v.Y, r));
                }
            });
        }
        ct.ThrowIfCancellationRequested();

        // Which layers: those asked for, or every bound drawing layer with copper — one per band,
        // the band's first drawing layer, because a band is ONE copper layer however many drawing
        // layers are bound to it.
        var requested = options.Layers ?? [.. bands.Where(b => subjects.ContainsKey(b.Index))
                                                     .Select(b => b.Layer.DrawingLayers.First(dl => bandOf[dl] == b))];
        var analysed = new List<(LayerKey Key, CrossSectionExtractor.Band Band)>();
        var notes = new List<string>();
        foreach (var key in requested)
        {
            if (!bandOf.TryGetValue(key, out var band))
            {
                notes.Add($"'{LayerName(key)}' is not bound to a conductor of the stackup, so it has no height " +
                          "and was not analysed.");
                continue;
            }
            if (analysed.Any(a => a.Band.Index == band.Index)) continue;
            analysed.Add((key, band));
        }
        // Top of the stack first, as the stackup lists them and as a reviewer reads a board.
        analysed.Sort((a, b) => a.Band.Index.CompareTo(b.Band.Index));
        if (analysed.Count == 0)
            return TraceImpedanceReport.Refused(
                "None of the layers asked for is a copper layer bound to the stackup, so there is nothing to analyse.");

        var copper = new Dictionary<int, TraceCopper>();
        var islandsOf = new Dictionary<int, (int[] IslandOfRing, double[] IslandArea, int[] HoleCount)>();
        foreach (var (index, list) in subjects)
        {
            ct.ThrowIfCancellationRequested();
            bool analysedBand = analysed.Any(a => a.Band.Index == index);
            if (!analysedBand)
            {
                copper[index] = new TraceCopper(Clipper.Union(list, LayoutClipper.Rule), indexed: true);
                continue;
            }
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Union, list, new Paths64(), tree, LayoutClipper.Rule);
            var rings = new Paths64();
            var ringIsland = new List<int>();
            var areas = new List<double>();
            var holes = new List<int>();
            void Walk(PolyPath64 node)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    var outer = node[i];
                    int island = areas.Count;
                    double area = Math.Abs(Clipper.Area(outer.Polygon!));
                    rings.Add(outer.Polygon!); ringIsland.Add(island);
                    int h = 0;
                    for (int j = 0; j < outer.Count; j++)
                    {
                        var hole = outer[j];
                        rings.Add(hole.Polygon!); ringIsland.Add(island);
                        area -= Math.Abs(Clipper.Area(hole.Polygon!));
                        h++;
                    }
                    areas.Add(area);
                    holes.Add(h);
                    for (int j = 0; j < outer.Count; j++) Walk(outer[j]);
                }
            }
            Walk(tree);
            copper[index] = new TraceCopper(rings, indexed: true);
            islandsOf[index] = ([.. ringIsland], [.. areas], [.. holes]);
        }

        var ctx = new TraceStack
        {
            Stack = stack, Bands = bands, BandOf = bandOf, Copper = copper, Tech = tech, DbuPerMicron = dbuPerMicron,
        };

        // ── layer by layer ──────────────────────────────────────────────────────────────────────
        // Each layer is found, cut, solved and assembled before the next is started, so a run that
        // is cancelled still has every layer it FINISHED — and the report is written for those
        // (owner, 2026-09-25: a long run cancelled part-way must not throw away what it has done).
        // Progress: Completed/Total counts layers; the stage counts the layer's solves.
        int id = 0, stationCount = 0, solveCount = 0;
        var layers = new List<TraceLayerResult>();
        bool cancelled = false;
        for (int li = 0; li < analysed.Count; li++)
        {
            var (key, band) = analysed[li];
            string name = LayerName(key);
            string tag = $"{name} ({li + 1} of {analysed.Count})";
            try
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage($"{tag}: finding traces");
                var lw = FindLayer(key, name, band, copper, islandsOf, vias, options, bands, dbuPerMicron, ct);

                control?.BeginStage($"{tag}: cutting");
                var solves = new Dictionary<string, TraceCut>();
                stationCount += Cut(lw, ctx, solves, dbuPerMicron, ct);

                var keys = solves.Keys.ToArray();
                control?.BeginStage($"{tag}: solving", keys.Length, "cross-sections");
                var answers = new System.Collections.Concurrent.ConcurrentDictionary<string, (double C, double C0, string? Refusal)>();
                Parallel.ForEach(keys,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    k =>
                    {
                        answers[k] = TraceCrossSection.Solve(ctx, solves[k], ct);
                        control?.TickStage();
                    });
                ct.ThrowIfCancellationRequested();
                solveCount += keys.Length;

                var runs = new List<TraceRun>();
                foreach (var chain in lw.Chains)
                    runs.Add(Assemble(chain, lw, answers, options, dbuPerMicron, ref id));
                layers.Add(new TraceLayerResult
                {
                    Layer = lw.Key,
                    Name = lw.Name,
                    Traces = runs,
                    PoursSkipped = lw.Pours,
                    MaxWidth = lw.MaxWidth,
                    Copper = lw.Copper is null ? [] : [.. lw.Copper.Paths.Select(p =>
                    {
                        var xy = new long[p.Count * 2];
                        for (int i = 0; i < p.Count; i++) { xy[2 * i] = p[i].X; xy[2 * i + 1] = p[i].Y; }
                        return xy;
                    })],
                });
                control?.Tick();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                var rest = analysed.Skip(li).Select(a => $"'{LayerName(a.Key)}'");
                notes.Add($"Cancelled after {li} of {analysed.Count} layer{(analysed.Count == 1 ? "" : "s")}; " +
                          $"not analysed: {string.Join(", ", rest)}.");
                break;
            }
        }

        if (!cancelled && layers.All(l => l.Traces.Count == 0))
            notes.Add("No traces were found on the layers analysed: no copper there has two long parallel " +
                      "edges facing each other outside a pour.");

        return new TraceImpedanceReport
        {
            TechnologyName = tech.Name,
            Technology = tech,
            TargetOhms = options.TargetOhms,
            TolerancePercent = options.TolerancePercent,
            DbuPerMicron = dbuPerMicron,
            DisplayUnit = options.DisplayUnit ?? LayoutUnit.Um,
            Layers = layers,
            LayersRequested = [.. analysed.Select(a => LayerName(a.Key))],
            Cancelled = cancelled,
            Extent = extent,
            Notes = notes,
            StationCount = stationCount,
            SolveCount = solveCount,
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>Pieces, pours and chains on one layer.</summary>
    private static LayerWork FindLayer(
        LayerKey key, string name, CrossSectionExtractor.Band band,
        Dictionary<int, TraceCopper> copper,
        Dictionary<int, (int[] IslandOfRing, double[] IslandArea, int[] HoleCount)> islandsOf,
        Dictionary<int, List<(double X, double Y, double R)>> vias,
        TraceImpedanceOptions options, List<CrossSectionExtractor.Band> bands, int dbuPerMicron,
        CancellationToken ct)
    {
        if (!copper.TryGetValue(band.Index, out var cu))
            return new LayerWork(key, name, band, null, [], 0, 0);

        var (minW, maxW) = WidthRange(options, bands, band, dbuPerMicron);
        var pieces = FindPieces(cu, minW, maxW, dbuPerMicron, ct);
        var (islandOfRing, islandArea, holeCount) = islandsOf[band.Index];
        var bandVias = vias.GetValueOrDefault(band.Index) ?? [];

        // Pours: pieces' area against the island's, and the vias landing on it.
        var pieceArea = new double[islandArea.Length];
        foreach (var p in pieces) pieceArea[islandOfRing[p.Ring]] += p.Length * p.Width;
        var viaCount = new int[islandArea.Length];
        foreach (var (vx, vy, _) in bandVias)
        {
            int e = cu.NearestEdge(vx, vy, maxW);
            if (e >= 0 && cu.Contains(vx, vy)) viaCount[islandOfRing[cu.RingOf[e]]]++;
        }
        var pour = new bool[islandArea.Length];
        int pours = 0;
        for (int i = 0; i < pour.Length; i++)
        {
            if (pieceArea[i] <= 0) continue;
            pour[i] = islandArea[i] > PourAreaRatio * pieceArea[i] || viaCount[i] >= PourViaCount
                      || (holeCount[i] >= 3 && islandArea[i] > 3 * pieceArea[i]);
            if (pour[i]) pours++;
        }
        pieces = [.. pieces.Where(p => !pour[islandOfRing[p.Ring]])];

        return new LayerWork(key, name, band, cu, Chain(pieces, cu, islandOfRing), pours, maxW)
        {
            Vias = bandVias, IslandOfRing = islandOfRing,
        };
    }

    /// <summary>The stations along every chain of one layer, their cuts, and the unique solves they
    /// need. Returns the station count.</summary>
    private static int Cut(LayerWork lw, TraceStack ctx, Dictionary<string, TraceCut> solves, int dbuPerMicron,
                           CancellationToken ct)
    {
        int stationCount = 0;
        foreach (var chain in lw.Chains)
        {
            ct.ThrowIfCancellationRequested();
            double sAt = 0;
            bool first = true;
            foreach (var (piece, reversed) in chain.Pieces)
            {
                var (ax, ay, bx, by) = reversed ? (piece.Bx, piece.By, piece.Ax, piece.Ay) : (piece.Ax, piece.Ay, piece.Bx, piece.By);
                if (!first) sAt += Math.Sqrt(Sq(ax - chain.LastX) + Sq(ay - chain.LastY));
                first = false;
                double len = piece.Length;
                double dx = (bx - ax) / len, dy = (by - ay) / len;
                double ux = -dy, uy = dx;
                int n = Math.Clamp((int)Math.Ceiling(len / (piece.Width / StationsPerWidth)), 1, MaxStationsPerPiece);
                double step = len / n;
                for (int i = 0; i < n; i++)
                {
                    double s = (i + 0.5) * step;
                    double qx = ax + s * dx, qy = ay + s * dy;
                    var st = new StationWork { S = sAt + s, Length = step, Ux = ux, Uy = uy };
                    if (lw.Copper!.ChordAt(qx, qy, ux, uy, 2 * lw.MaxWidth) is { } q && q.E0 >= 0 && q.E1 >= 0)
                    {
                        qx += 0.5 * (q.T0 + q.T1) * ux; qy += 0.5 * (q.T0 + q.T1) * uy;
                        double w = q.T1 - q.T0;
                        var cut = TraceCrossSection.Cut(ctx, lw.Band, qx, qy, ux, uy, -0.5 * w, 0.5 * w,
                                                        double.MaxValue, Math.Max(1.0 * dbuPerMicron, 0.015 * w));
                        st.Cut = cut;
                        if (cut.Refusal is null) solves.TryAdd(cut.Key, cut);
                    }
                    st.X = qx; st.Y = qy;
                    chain.Stations.Add(st);
                    stationCount++;
                }
                sAt += len;
                chain.LastX = bx; chain.LastY = by;
            }
            chain.Length = sAt;
        }
        return stationCount;
    }

    private static double Sq(double v) => v * v;

    /// <summary>The narrowest and widest copper read as a trace on <paramref name="band"/>, DBU — both
    /// from the distance to the nearest other conductor: a sliver a twentieth of that is an artefact of
    /// the union, and a strip ten times it is a pour's neck, not a line.</summary>
    private static (double Min, double Max) WidthRange(TraceImpedanceOptions options,
        List<CrossSectionExtractor.Band> bands, CrossSectionExtractor.Band band, int dbuPerMicron)
    {
        double nearest = double.MaxValue;
        foreach (var b in bands)
        {
            if (b.Index == band.Index) continue;
            double gap = b.TopM <= band.BottomM ? band.BottomM - b.TopM
                       : b.BottomM >= band.TopM ? b.BottomM - band.TopM : double.MaxValue;
            nearest = Math.Min(nearest, gap);
        }
        double nearestUm = nearest == double.MaxValue ? 800 : nearest * 1e6;
        double minUm = Math.Max(1, 0.05 * nearestUm);
        double maxUm = options.MaxWidthMicrons is { } mw && mw > 0 ? mw : Math.Clamp(10 * nearestUm, 1000, 8000);
        return (minUm * dbuPerMicron, maxUm * dbuPerMicron);
    }

    // ── 1. pieces ───────────────────────────────────────────────────────────────────────────────

    private sealed record Piece(double Ax, double Ay, double Bx, double By, double Width, int Ring, int EdgeA, int EdgeB)
    {
        public double Length => Math.Sqrt(Sq(Bx - Ax) + Sq(By - Ay));
    }

    private static List<Piece> FindPieces(TraceCopper cu, double minW, double maxW, int dbuPerMicron, CancellationToken ct)
    {
        int n = cu.Ax.Length;
        double minEdge = 10.0 * dbuPerMicron;
        double cosTol = Math.Cos(ParallelToleranceDeg * Math.PI / 180);
        double probe = Math.Max(1, 0.25 * dbuPerMicron);

        // Unit direction and inward (copper-side) normal of every long edge.
        var dx = new double[n]; var dy = new double[n]; var nx = new double[n]; var ny = new double[n];
        var len = new double[n];
        var longEdge = new bool[n];
        for (int e = 0; e < n; e++)
        {
            len[e] = cu.Length(e);
            if (len[e] < minEdge) continue;
            longEdge[e] = true;
            dx[e] = (cu.Bx[e] - cu.Ax[e]) / len[e]; dy[e] = (cu.By[e] - cu.Ay[e]) / len[e];
            nx[e] = -dy[e]; ny[e] = dx[e];
            double mx = 0.5 * (cu.Ax[e] + cu.Bx[e]), my = 0.5 * (cu.Ay[e] + cu.By[e]);
            if (!cu.Contains(mx + probe * nx[e], my + probe * ny[e])) { nx[e] = -nx[e]; ny[e] = -ny[e]; }
        }

        var pieces = new List<Piece>();
        for (int e = 0; e < n; e++)
        {
            if (!longEdge[e]) continue;
            if ((e & 255) == 0) ct.ThrowIfCancellationRequested();
            double ax = cu.Ax[e], ay = cu.Ay[e];
            double x0 = Math.Min(ax, cu.Bx[e]), x1 = Math.Max(ax, cu.Bx[e]);
            double y0 = Math.Min(ay, cu.By[e]), y1 = Math.Max(ay, cu.By[e]);
            var near = cu.EdgesNear(Math.Min(x0, x0 + maxW * nx[e]), Math.Min(y0, y0 + maxW * ny[e]),
                                    Math.Max(x1, x1 + maxW * nx[e]), Math.Max(y1, y1 + maxW * ny[e]));
            foreach (int f in near)
            {
                if (f <= e || !longEdge[f]) continue;
                if (dx[e] * dx[f] + dy[e] * dy[f] > -cosTol) continue;   // anti-parallel only

                // f on e's copper side, at width w; e on f's copper side.
                double da = (cu.Ax[f] - ax) * nx[e] + (cu.Ay[f] - ay) * ny[e];
                double db = (cu.Bx[f] - ax) * nx[e] + (cu.By[f] - ay) * ny[e];
                if (da < minW || db < minW || da > maxW || db > maxW) continue;
                if ((ax - cu.Ax[f]) * nx[f] + (ay - cu.Ay[f]) * ny[f] <= 0) continue;
                double w = 0.5 * (da + db);
                if (Math.Abs(da - db) > Math.Max(0.05 * w, 2.0 * dbuPerMicron)) continue;

                // Overlap along e.
                double pa = (cu.Ax[f] - ax) * dx[e] + (cu.Ay[f] - ay) * dy[e];
                double pb = (cu.Bx[f] - ax) * dx[e] + (cu.By[f] - ay) * dy[e];
                double o0 = Math.Max(0, Math.Min(pa, pb)), o1 = Math.Min(len[e], Math.Max(pa, pb));
                if (o1 - o0 < Math.Max(minEdge, 0.5 * w)) continue;

                // Nothing between: a ray from e at the quarter, middle and three-quarter points hits f.
                bool clear = true;
                foreach (double frac in (ReadOnlySpan<double>)[0.25, 0.5, 0.75])
                {
                    double s = o0 + frac * (o1 - o0);
                    double px = ax + s * dx[e] + probe * nx[e], py = ay + s * dy[e] + probe * ny[e];
                    if (cu.ChordAt(px, py, nx[e], ny[e], maxW * 1.5) is not { } c || c.E1 != f) { clear = false; break; }
                }
                if (!clear) continue;

                double hx = 0.5 * w * nx[e], hy = 0.5 * w * ny[e];
                pieces.Add(new Piece(ax + o0 * dx[e] + hx, ay + o0 * dy[e] + hy,
                                     ax + o1 * dx[e] + hx, ay + o1 * dy[e] + hy, w, cu.RingOf[e], e, f));
            }
        }
        return pieces;
    }

    // ── 3. chains ───────────────────────────────────────────────────────────────────────────────

    private sealed class ChainWork
    {
        public List<(Piece Piece, bool Reversed)> Pieces { get; } = [];
        public List<StationWork> Stations { get; } = [];
        public string StartsAt = "", EndsAt = "";
        public bool StartJunction, EndJunction;
        public double LastX, LastY, Length;
    }

    private sealed class StationWork
    {
        public double X, Y, Ux, Uy, S, Length;
        public TraceCut? Cut;
    }

    private sealed record LayerWork(
        LayerKey Key, string Name, CrossSectionExtractor.Band Band, TraceCopper? Copper,
        List<ChainWork> Chains, int Pours, double MaxWidth)
    {
        public List<(double X, double Y, double R)> Vias { get; init; } = [];
        public int[] IslandOfRing { get; init; } = [];
    }

    private static List<ChainWork> Chain(List<Piece> pieces, TraceCopper cu, int[] islandOfRing)
    {
        int m = pieces.Count;
        // Ends: 2i is piece i's A end, 2i+1 its B end.
        (double X, double Y) End(int k) => k % 2 == 0 ? (pieces[k / 2].Ax, pieces[k / 2].Ay) : (pieces[k / 2].Bx, pieces[k / 2].By);

        var candidates = new List<int>[2 * m];
        for (int k = 0; k < 2 * m; k++) candidates[k] = [];

        // Sort ends by x so the neighbour search is a sweep, not all pairs.
        var order = Enumerable.Range(0, 2 * m).OrderBy(k => End(k).X).ToArray();
        double maxWidth = pieces.Count == 0 ? 0 : pieces.Max(p => p.Width);
        for (int a = 0; a < order.Length; a++)
        {
            int i = order[a];
            var (ix, iy) = End(i);
            for (int b = a + 1; b < order.Length; b++)
            {
                int j = order[b];
                var (jx, jy) = End(j);
                if (jx - ix > 1.25 * maxWidth) break;
                if (i / 2 == j / 2) continue;
                var pi = pieces[i / 2]; var pj = pieces[j / 2];
                double r = 1.25 * Math.Max(pi.Width, pj.Width);
                double d2 = Sq(jx - ix) + Sq(jy - iy);
                if (d2 > r * r) continue;
                if (islandOfRing[pi.Ring] != islandOfRing[pj.Ring]) continue;
                if (!cu.Contains(0.5 * (ix + jx), 0.5 * (iy + jy))) continue;
                // The two pieces must LEAVE the meeting point in different directions: an end that
                // meets the side of a parallel piece running alongside is not a continuation.
                var (oix, oiy) = End(i ^ 1); var (ojx, ojy) = End(j ^ 1);
                double dix = oix - ix, diy = oiy - iy, djx = ojx - jx, djy = ojy - jy;
                double cos = (dix * djx + diy * djy) / Math.Max(1e-9, Math.Sqrt((dix * dix + diy * diy) * (djx * djx + djy * djy)));
                if (cos > 0.2) continue;
                candidates[i].Add(j);
                candidates[j].Add(i);
            }
        }

        var link = new int[2 * m];
        var junction = new bool[2 * m];
        for (int k = 0; k < 2 * m; k++)
        {
            link[k] = -1;
            if (candidates[k].Count >= 2) junction[k] = true;
        }
        // A junction ends every trace that meets it.
        for (int k = 0; k < 2 * m; k++)
            if (junction[k]) foreach (int j in candidates[k]) junction[j] = true;
        for (int k = 0; k < 2 * m; k++)
        {
            if (junction[k] || candidates[k].Count != 1) continue;
            int j = candidates[k][0];
            if (junction[j] || candidates[j].Count != 1 || candidates[j][0] != k) continue;
            link[k] = j;
        }

        var used = new bool[m];
        var chains = new List<ChainWork>();
        void Walk(int startEnd)
        {
            var chain = new ChainWork { StartJunction = junction[startEnd] };
            int end = startEnd;
            while (true)
            {
                int p = end / 2;
                if (used[p]) break;
                used[p] = true;
                bool reversed = end % 2 == 1;   // entering at B → walk B→A
                chain.Pieces.Add((pieces[p], reversed));
                int exit = end ^ 1;
                chain.EndJunction = junction[exit];
                if (link[exit] < 0) break;
                end = link[exit];
            }
            chains.Add(chain);
        }
        for (int k = 0; k < 2 * m; k++)
            if (!used[k / 2] && link[k] < 0) Walk(k);
        for (int k = 0; k < 2 * m; k++)          // closed loops, if any
            if (!used[k / 2]) Walk(k);

        // A piece at a chain's END that is shorter than it is wide is the land the trace runs onto — a
        // component pad the trace overlaps, joined to it as a "width step" — not a line: round 8's
        // 0201 pads read 57.6 Ω as the last 18.7 mil of a 50 Ω trace. Trimmed, repeatedly, from both
        // ends; a piece like that INSIDE a chain is a genuine step and stays.
        foreach (var c in chains)
        {
            while (c.Pieces.Count > 1 && c.Pieces[0].Piece.Length < c.Pieces[0].Piece.Width) { c.Pieces.RemoveAt(0); c.StartJunction = false; }
            while (c.Pieces.Count > 1 && c.Pieces[^1].Piece.Length < c.Pieces[^1].Piece.Width) { c.Pieces.RemoveAt(c.Pieces.Count - 1); c.EndJunction = false; }
        }

        // A chain shorter than MinAspect widths is a pad; one piece shorter than a width is a corner.
        return [.. chains.Where(c =>
        {
            double length = c.Pieces.Sum(p => p.Piece.Length);
            double width = c.Pieces.Max(p => p.Piece.Width);
            return length >= MinAspect * width && c.Pieces.Any(p => p.Piece.Length >= width);
        })];
    }

    // ── 5. findings ─────────────────────────────────────────────────────────────────────────────

    private static string EndKind(LayerWork lw, double x, double y, double dirX, double dirY, double w, bool junction)
    {
        if (junction) return "junction";
        foreach (var (vx, vy, r) in lw.Vias)
            if (Math.Sqrt(Sq(vx - x) + Sq(vy - y)) <= r + w) return "via";
        // Walk out along the trace's own direction for two widths: copper that widens past half as
        // much again is a pad; copper that stops at once is the end of the copper, unless it goes on
        // in some other forward direction (a curve, or a bend too gentle to have been joined).
        for (int k = 1; k <= 8; k++)
        {
            double f = 0.25 * k * w;
            double px = x + dirX * f, py = y + dirY * f;
            if (!lw.Copper!.Contains(px, py))
            {
                if (k > 1) return "continues";
                for (int j = -4; j <= 4; j++)
                {
                    double a = j * Math.PI / 9, cs = Math.Cos(a), sn = Math.Sin(a);
                    double qx = x + w * (cs * dirX - sn * dirY), qy = y + w * (sn * dirX + cs * dirY);
                    if (j != 0 && lw.Copper.Contains(qx, qy)) return "continues";
                }
                return "open end";
            }
            if (lw.Copper.ChordAt(px, py, -dirY, dirX, 4 * lw.MaxWidth) is { } c && c.T1 - c.T0 > 1.5 * w) return "pad";
        }
        return "continues";
    }

    private static TraceRun Assemble(
        ChainWork chain, LayerWork lw,
        System.Collections.Concurrent.ConcurrentDictionary<string, (double C, double C0, string? Refusal)> answers,
        TraceImpedanceOptions options, int dbuPerMicron, ref int id)
    {
        var fmt = new TraceImpedanceReport { DbuPerMicron = dbuPerMicron, DisplayUnit = options.DisplayUnit ?? LayoutUnit.Um };
        double lo = options.TargetOhms * (1 - options.TolerancePercent / 100);
        double hi = options.TargetOhms * (1 + options.TolerancePercent / 100);

        var stations = new List<TraceStation>();
        foreach (var st in chain.Stations)
        {
            var cut = st.Cut;
            double? z0 = null, eeff = null;
            string? refusal = cut is null ? "the cut left the copper" : cut.Refusal;
            if (cut is not null && cut.Refusal is null && answers.TryGetValue(cut.Key, out var a))
            {
                if (a.Refusal is null) { z0 = TraceCrossSection.Z0(a.C, a.C0); eeff = a.C / a.C0; }
                else refusal = a.Refusal;
            }
            stations.Add(new TraceStation
            {
                X = (long)Math.Round(st.X), Y = (long)Math.Round(st.Y), Ux = st.Ux, Uy = st.Uy,
                S = st.S, Length = st.Length,
                Width = cut?.Width ?? 0,
                Z0 = z0, Eeff = eeff,
                Configuration = cut?.Configuration,
                ReferenceBelow = cut?.LowerRef?.Layer.Name ?? (cut?.Plane == true ? "stackup bottom ground" : null),
                ReferenceAbove = cut?.UpperRef?.Layer.Name,
                GapLeft = cut?.GapL, GapRight = cut?.GapR,
                Refusal = refusal,
            });
        }

        var first = chain.Pieces[0]; var last = chain.Pieces[^1];
        var (sx, sy, sdx, sdy) = first.Reversed
            ? (first.Piece.Bx, first.Piece.By, first.Piece.Bx - first.Piece.Ax, first.Piece.By - first.Piece.Ay)
            : (first.Piece.Ax, first.Piece.Ay, first.Piece.Ax - first.Piece.Bx, first.Piece.Ay - first.Piece.By);
        var (ex, ey, edx, edy) = last.Reversed
            ? (last.Piece.Ax, last.Piece.Ay, last.Piece.Ax - last.Piece.Bx, last.Piece.Ay - last.Piece.By)
            : (last.Piece.Bx, last.Piece.By, last.Piece.Bx - last.Piece.Ax, last.Piece.By - last.Piece.Ay);
        double sl = Math.Max(1e-9, Math.Sqrt(sdx * sdx + sdy * sdy)), el = Math.Max(1e-9, Math.Sqrt(edx * edx + edy * edy));
        string startsAt = EndKind(lw, sx, sy, sdx / sl, sdy / sl, first.Piece.Width, chain.StartJunction);
        string endsAt = EndKind(lw, ex, ey, edx / el, edy / el, last.Piece.Width, chain.EndJunction);

        // ── the findings ────────────────────────────────────────────────────────────────────────
        var issues = new List<TraceIssue>();
        var runNotes = new List<string>();

        void Runs(Func<int, bool> flagged, Action<int, int> emit)
        {
            int i = 0;
            while (i < stations.Count)
            {
                if (!flagged(i)) { i++; continue; }
                int j = i;
                while (j + 1 < stations.Count && flagged(j + 1)) j++;
                emit(i, j);
                i = j + 1;
            }
        }
        // From the start of station i's stretch to the end of station j's, along the trace.
        (long, long, long, long) Span(int i, int j)
        {
            var a = stations[i]; var b = stations[j];
            return ((long)Math.Round(a.X - 0.5 * a.Length * a.Uy), (long)Math.Round(a.Y + 0.5 * a.Length * a.Ux),
                    (long)Math.Round(b.X + 0.5 * b.Length * b.Uy), (long)Math.Round(b.Y - 0.5 * b.Length * b.Ux));
        }
        double SpanLen(int i, int j) => stations[j].S - stations[i].S + 0.5 * (stations[i].Length + stations[j].Length);

        // Z0 outside the band.
        Runs(i => stations[i].Z0 is { } z && (z < lo || z > hi), (i, j) =>
        {
            var zs = stations.Skip(i).Take(j - i + 1).Select(s => s.Z0!.Value).ToList();
            var (x0, y0, x1, y1) = Span(i, j);
            string range = zs.Min() == zs.Max() || Math.Abs(zs.Max() - zs.Min()) < 0.05
                ? $"{zs[0]:0.0} Ω" : $"{zs.Min():0.0}–{zs.Max():0.0} Ω";
            issues.Add(new TraceIssue(TraceIssueKind.OutOfTolerance, x0, y0, x1, y1,
                $"Z0 {range} over {fmt.Len(SpanLen(i, j))} from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}, outside " +
                $"{options.TargetOhms:0.#} Ω ± {options.TolerancePercent:0.#} %."));
        });

        // The nearest layer on each side, walked along the whole trace.
        void Side(Func<TraceCut, List<CrossSectionExtractor.Band>> sideOf,
                  Func<TraceCut, CrossSectionExtractor.Band?> refOf,
                  Func<TraceCut, List<(CrossSectionExtractor.Band Band, double Cov)>> skippedOf, string where)
        {
            var cuts = chain.Stations.Select(s => s.Cut).ToList();
            var nearest = cuts.FirstOrDefault(c => c is not null) is { } c0 ? sideOf(c0).FirstOrDefault() : null;
            if (nearest is null) return;

            // Per station: 2 covered, 1 partly, 0 missing, −1 not cut.
            int Status(int i) => cuts[i] is not { } c ? -1
                : ReferenceEquals(refOf(c), nearest) ? 2
                : skippedOf(c).FirstOrDefault().Cov > 0.005 ? 1 : 0;
            var status = Enumerable.Range(0, cuts.Count).Select(Status).ToArray();
            bool anyCovered = status.Any(s => s == 2);
            bool anyPartial = status.Any(s => s == 1);

            if (!anyCovered && !anyPartial)
            {
                var refs = cuts.Where(c => c is not null).Select(c => refOf(c!)?.Layer.Name).Distinct().ToList();
                string then = refs.Count == 1 && refs[0] is { } r ? $", so the reference {where} is '{r}'" : "";
                runNotes.Add($"'{nearest.Layer.Name}' has no copper {where} this trace anywhere along it{then} — " +
                             "what a layer cleared under a trace on purpose looks like.");
            }
            else
            {
                Runs(i => status[i] == 0, (i, j) =>
                {
                    var (x0, y0, x1, y1) = Span(i, j);
                    string? refThere = cuts[i] is { } c ? refOf(c)?.Layer.Name : null;
                    issues.Add(new TraceIssue(TraceIssueKind.ReturnBroken, x0, y0, x1, y1,
                        $"'{nearest.Layer.Name}' is missing {where} the trace for {fmt.Len(SpanLen(i, j))}, from " +
                        $"{fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}, but present elsewhere along it — the return path " +
                        $"is broken there ({(refThere is null ? $"no reference {where} there" : $"the reference there is '{refThere}'")})."));
                });
            }
            Runs(i => status[i] == 1, (i, j) =>
            {
                var (x0, y0, x1, y1) = Span(i, j);
                double cov = skippedOf(cuts[i]!).First().Cov;
                issues.Add(new TraceIssue(TraceIssueKind.PartialReference, x0, y0, x1, y1,
                    $"'{nearest.Layer.Name}' covers only {cov:P0} of the width {where} the trace for " +
                    $"{fmt.Len(SpanLen(i, j))}, from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}: copper edge under the trace."));
            });

            // The reference stepping between two stations neither of which the nearest layer covers.
            for (int i = 1; i < cuts.Count; i++)
            {
                if (cuts[i - 1] is not { } a || cuts[i] is not { } b) continue;
                if (status[i - 1] == 2 || status[i] == 2) continue;
                string? ra = refOf(a)?.Layer.Name, rb = refOf(b)?.Layer.Name;
                if (ra == rb || ra is null || rb is null) continue;
                var s = stations[i];
                issues.Add(new TraceIssue(TraceIssueKind.ReferenceStep, s.X, s.Y, s.X, s.Y,
                    $"The reference {where} steps from '{ra}' to '{rb}' at {fmt.Pt(s.X, s.Y)}."));
            }
        }
        if (chain.Stations.Select(st => st.Cut?.ImpliedBelow).FirstOrDefault(b => b is not null) is { } implied)
            runNotes.Add($"Nothing is drawn on '{implied.Layer.Name}', which the technology marks as the ground " +
                         "reference, so it was taken as a solid plane under the trace.");
        Side(c => c.Below, c => c.LowerRef, c => c.SkippedBelow, "below");
        Side(c => c.Above, c => c.UpperRef, c => c.SkippedAbove, "above");

        // No reference at all, and cuts that could not be solved.
        Runs(i => stations[i].Refusal == "no return conductor", (i, j) =>
        {
            var (x0, y0, x1, y1) = Span(i, j);
            issues.Add(new TraceIssue(TraceIssueKind.NoReference, x0, y0, x1, y1,
                $"No copper covers the trace on any layer, and nothing is beside it, for {fmt.Len(SpanLen(i, j))} " +
                $"from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}: there is no return path to take an impedance against."));
        });
        Runs(i => stations[i].Refusal is { } r && r != "no return conductor", (i, j) =>
        {
            var (x0, y0, x1, y1) = Span(i, j);
            issues.Add(new TraceIssue(TraceIssueKind.Unsolved, x0, y0, x1, y1,
                $"The cross-section could not be solved over {fmt.Len(SpanLen(i, j))} from {fmt.Pt(x0, y0)}: {stations[i].Refusal}."));
        });

        // ── the numbers ─────────────────────────────────────────────────────────────────────────
        var solved = stations.Where(s => s.Z0 is not null).ToList();
        double solvedLen = solved.Sum(s => s.Length);
        double inTol = solved.Where(s => s.Z0 >= lo && s.Z0 <= hi).Sum(s => s.Length);
        var configurations = solvedLen <= 0 ? [] : solved.GroupBy(s => s.Configuration ?? "")
            .Select(g => (Name: g.Key, Share: g.Sum(s => s.Length) / solvedLen))
            .OrderByDescending(c => c.Share)
            .Where(c => c.Share >= 0.02)     // a station or two at a pad's edge is not a second type
            .ToList();
        string config = configurations.FirstOrDefault().Name ?? "";
        var references = stations.SelectMany(s => new[] { s.ReferenceBelow, s.ReferenceAbove })
                                 .Where(r => r is not null).Distinct().Select(r => r!).ToList();

        id++;
        return new TraceRun
        {
            Id = $"T{id}",
            Layer = lw.Key,
            LayerName = lw.Name,
            Stations = stations,
            Pieces = [.. chain.Pieces.Select(p => p.Reversed
                ? ((long)Math.Round(p.Piece.Bx), (long)Math.Round(p.Piece.By), (long)Math.Round(p.Piece.Ax), (long)Math.Round(p.Piece.Ay), p.Piece.Width)
                : ((long)Math.Round(p.Piece.Ax), (long)Math.Round(p.Piece.Ay), (long)Math.Round(p.Piece.Bx), (long)Math.Round(p.Piece.By), p.Piece.Width))],
            StartX = (long)Math.Round(sx), StartY = (long)Math.Round(sy),
            EndX = (long)Math.Round(ex), EndY = (long)Math.Round(ey),
            StartsAt = startsAt, EndsAt = endsAt,
            Length = chain.Length,
            WidthMin = chain.Pieces.Min(p => p.Piece.Width),
            WidthMax = chain.Pieces.Max(p => p.Piece.Width),
            Z0Min = solved.Count > 0 ? solved.Min(s => s.Z0) : null,
            Z0Max = solved.Count > 0 ? solved.Max(s => s.Z0) : null,
            Z0Mean = solvedLen > 0 ? solved.Sum(s => s.Z0!.Value * s.Length) / solvedLen : null,
            InTolerance = solvedLen > 0 ? inTol / solvedLen : 0,
            Configuration = config,
            Configurations = configurations,
            References = references,
            Issues = issues,
            Notes = runNotes,
            Verdict = solved.Count == 0 ? TraceVerdict.Unsolved
                    : issues.Count > 0 ? TraceVerdict.Fail : TraceVerdict.Pass,
        };
    }
}
