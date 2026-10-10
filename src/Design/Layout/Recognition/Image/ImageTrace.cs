// Trace a layout picture — brief-img-3-trace-layout-image.md R-im3-1, R-im3-5, R-im3-6, R-im3-9 (D5, D6, D7, D8, D16).
//
// A layout picture becomes LayoutShapes on technology layers, at a stated scale, sitting exactly on the picture. The
// split ArtworkRecognition already has: Trace is pure — it posts nothing and writes nothing — and Run traces and then
// writes (ImageTraceTarget.cs).
//
//   picture ─ IM-1 colour clusters ─ ImageLayerMap (Auto, or the user's) ─ per layer: coverage → contours (IM-1)
//           ─ ImageScale (D6) ─ PixelFrame ─ DBU: rectangles, circles, polygons with their holes, vias, outline paths
//           ─ the underlay: the picture itself, locked, at 35 %, at exactly the traced frame
//
// Every coordinate leaves pixel space through ONE PixelFrame, and is rounded so a snapped edge stays snapped: an axis
// edge keeps one coordinate and a 45° edge keeps x ± y, both rounded once per edge rather than once per vertex —
// rounding the two ends' x and y separately would put a 1-DBU kink in an exact 45° jog.
//
// A trace is refused only for no technology or no shape on any copper layer; Run also refuses no scale.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>How a picture is traced. Every field has the dialog's default.</summary>
public sealed record ImageTraceOptions
{
    /// <summary>Douglas–Peucker tolerance on each contour, pixels.</summary>
    public double SimplifyPx { get; init; } = 0.35;

    /// <summary>Whether edges near 0°/90° (and 45°) are snapped onto them.</summary>
    public bool Snap { get; init; } = true;

    /// <summary>An edge within this many degrees of a snap angle is snapped.</summary>
    public double SnapAngleDeg { get; init; } = 3.0;

    /// <summary>Also snap onto 45° and 135°.</summary>
    public bool Snap45 { get; init; } = true;

    /// <summary>Regions and holes smaller than this many square pixels are specks and are removed.</summary>
    public double MinFeaturePx2 { get; init; } = 2.0;

    /// <summary>Keep the source picture under the result (D5; Settings ▸ Recognition).</summary>
    public bool KeepUnderlay { get; init; } = true;

    /// <summary>At most this many colours are separated (D7).</summary>
    public int MaxColours { get; init; } = 8;

    /// <summary>Colours closer than this ΔE are one colour (D7).</summary>
    public double MergeDeltaE { get; init; } = DefaultMergeDeltaE;

    public const double DefaultMergeDeltaE = 10.0;

    /// <summary>The provenance's options block. The merge distance is written only when it is not the default, so a
    /// document traced with every default reads as it always has.</summary>
    public SortedDictionary<string, string> ToProvenance() => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["simplify"] = SimplifyPx.ToString("0.###", CultureInfo.InvariantCulture) + " px",
        ["snap"] = Snap ? (Snap45 ? "0/45/90" : "0/90") : "off",
        ["snapAngle"] = SnapAngleDeg.ToString("0.###", CultureInfo.InvariantCulture) + " deg",
        ["minFeature"] = MinFeaturePx2.ToString("0.###", CultureInfo.InvariantCulture) + " px2",
        ["colours"] = MaxColours.ToString(CultureInfo.InvariantCulture),
        ["underlay"] = KeepUnderlay ? "on" : "off",
    }.WithMerge(MergeDeltaE);
}

internal static class ImageTraceOptionsProvenance
{
    public static SortedDictionary<string, string> WithMerge(this SortedDictionary<string, string> d, double merge)
    {
        if (merge != ImageTraceOptions.DefaultMergeDeltaE) d["merge"] = merge.ToString("0.###", CultureInfo.InvariantCulture) + " dE";
        return d;
    }
}

/// <summary>A rectangle in the picture, pixels (y down) — a trace scope.</summary>
public readonly record struct PixelRect(double Left, double Top, double Right, double Bottom);

/// <summary>What a trace reads (R-im3-1).</summary>
public sealed record ImageTraceInput
{
    /// <summary>The picture (IM-2).</summary>
    public required ImageSource Source { get; init; }

    /// <summary>The technology the user chose (D8), or null — which is a refusal.</summary>
    public required Technology? Technology { get; init; }

    /// <summary>Its file, for the written layout's technology reference.</summary>
    public string? TechnologyPath { get; init; }

    /// <summary>What the user said about the scale; Auto lets the evidence decide (D6).</summary>
    public ImageScaleStatement Scale { get; init; } = ImageScaleStatement.Auto;

    /// <summary>The colour → layer map; null is Auto. An edited map is rebound to the clusters, never replaced.</summary>
    public ImageLayerMap? LayerMap { get; init; }

    /// <summary>The part of the picture traced, pixels; null is the whole picture.</summary>
    public PixelRect? Scope { get; init; }

    public ImageTraceOptions Options { get; init; } = new();

    /// <summary>The kind as IM-2 read (or the user forced) it; null reads it again for the provenance.</summary>
    public ImageKindResult? Kind { get; init; }

    /// <summary>The trace goes into the layout the picture's bitmap sits in (D6 (1), D14). <see cref="ImageTrace.Run"/>
    /// sets it from the target.</summary>
    public bool TargetIsPlacedLayout { get; init; }

    /// <summary>The DBU per micron of a new layout. A placed bitmap's layout uses its own.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;
}

/// <summary>One layer as traced.</summary>
/// <param name="Name">The technology layer's name.</param>
/// <param name="Key">Its key, or null when the technology has no such layer (nothing is written for it).</param>
/// <param name="PixelRegions">The regions in pixel space — what the dialog's overlay draws.</param>
/// <param name="Shapes">The shapes in DBU; empty until there is a scale.</param>
public sealed record ImageTracedLayer(string Name, LayerKey? Key, ImageLayerRole Role,
                                      IReadOnlyList<ContourRegion> PixelRegions, IReadOnlyList<LayoutShape> Shapes);

/// <summary>What a trace produced.</summary>
public sealed record ImageTraceResult
{
    /// <summary>Why nothing can be made, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public required RecognitionReport Report { get; init; }

    public ColourClusterSet? Clusters { get; init; }
    public ImageLayerMap? LayerMap { get; init; }

    /// <summary>Every piece of scale evidence, and the one chosen (null: no scale yet).</summary>
    public IReadOnlyList<ImageScaleCandidate> ScaleCandidates { get; init; } = [];
    public ImageScaleCandidate? Scale { get; init; }

    /// <summary>Pixels → DBU; null without a scale.</summary>
    public PixelFrame? Frame { get; init; }
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    public IReadOnlyList<ImageTracedLayer> Layers { get; init; } = [];

    /// <summary>The drill holes read, pixels — what the dialog's overlay draws before there is a scale.</summary>
    public IReadOnlyList<CircleFit> Drills { get; init; } = [];

    /// <summary>Every traced shape, in DBU — vias and outline paths included, the underlay not.</summary>
    public IReadOnlyList<LayoutShape> Shapes { get; init; } = [];

    /// <summary>The picture as a locked 35 % bitmap at the traced frame (D5), or null. Its path is the source file's
    /// until <see cref="ImageTrace.Run"/> keeps a copy and points it there.</summary>
    public BitmapShape? Underlay { get; init; }

    /// <summary>The silkscreen's centre lines, DBU, flat x,y — what IM-4's stroke reader takes.</summary>
    public IReadOnlyList<long[]> SilkscreenStrokes { get; init; } = [];

    /// <summary>The silkscreen's median stroke width, pixels (IM-1's skeleton estimate); 0 without silkscreen.</summary>
    public double SilkscreenStrokeWidthPx { get; init; }

    /// <summary>The technology layer the silkscreen was mapped to, or null.</summary>
    public LayerKey? SilkscreenLayer { get; init; }

    /// <summary>The part of the picture traced, pixels: the scope, else the whole picture.</summary>
    public PixelRect Traced { get; init; }

    /// <summary>R-im3-3's one line, or null without a scale.</summary>
    public string? ResolutionLine { get; init; }

    public ImageKindResult? Kind { get; init; }
}

public static partial class ImageTrace
{
    /// <summary>The underlay's opacity (D5).</summary>
    public const double UnderlayOpacity = 0.35;

    /// <summary>The one sentence for a picture with no copper at the mapped colours.</summary>
    public const string NoCopperRefusal =
        "The picture has no copper-coloured regions at the mapped colours: map a colour to a copper layer, or check " +
        "that the picture is a layout.";

    public const string NoTechnologyRefusal =
        "There is no technology, so there are no layers to trace onto. Choose one — the workspace's default — and try again.";

    /// <summary>Traces <paramref name="input"/>'s picture (R-im3-1). Pure: posts nothing, writes nothing.</summary>
    public static ImageTraceResult Trace(ImageTraceInput input, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var report = new RecognitionReport();
        var token = control?.Token ?? CancellationToken.None;
        if (input.Technology is not { } tech) return new ImageTraceResult { Report = report, Refusal = NoTechnologyRefusal };

        control?.BeginStage("Separating colours");
        var prep = Prepare(input);
        token.ThrowIfCancellationRequested();

        var copperNames = ImageLayerMap.CopperLayers(tech).Select(k => ImageLayerMap.NameOf(tech, k)).ToHashSet(StringComparer.Ordinal);
        bool anyCopper = prep.Layers.Any(l => copperNames.Contains(l.Name) && l.Regions.Count > 0);

        control?.BeginStage("Finding the scale");
        var candidates = ImageScale.Candidates(input, prep, out string? scaleRefusal);
        var chosen = scaleRefusal is null ? ImageScale.Choose(candidates, input.TargetIsPlacedLayout) : null;
        token.ThrowIfCancellationRequested();

        var silk = prep.Layers.FirstOrDefault(l => l.Role == ImageLayerRole.Silkscreen);
        var result = new ImageTraceResult
        {
            Report = report, Clusters = prep.Clusters, LayerMap = prep.Map, ScaleCandidates = candidates, Scale = chosen,
            Drills = prep.Drills,
            Kind = input.Kind, SilkscreenStrokeWidthPx = silk?.StrokeWidth ?? 0, SilkscreenLayer = silk?.Key,
            Traced = input.Scope ?? new PixelRect(0, 0, input.Source.Raster.Width, input.Source.Raster.Height),
            Layers = [.. prep.Layers.Select(l => new ImageTracedLayer(l.Name, l.Key, l.Role, l.Regions, []))],
        };
        ReportColours(prep, report);
        if (scaleRefusal is not null) return result with { Refusal = scaleRefusal };
        if (!anyCopper) return result with { Refusal = NoCopperRefusal };

        report.Add(RecognitionFindingClass.ImageStackupNotRead, 1,
            $"The stackup is {tech.Name}'s: a picture says nothing about the stackup, and none was read from it.");
        if (chosen is null)
        {
            ReportPixels(prep, report);
            return result;
        }

        control?.BeginStage("Tracing");
        var built = Build(input, prep, chosen, report);
        token.ThrowIfCancellationRequested();
        return result with
        {
            Frame = built.Frame, DbuPerMicron = built.DbuPerMicron, Layers = built.Layers, Shapes = built.Shapes,
            Underlay = built.Underlay, SilkscreenStrokes = built.Strokes, ResolutionLine = built.Resolution,
        };
    }

    // ── stage 1: pixels ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One layer in pixel space.</summary>
    internal sealed record PixelLayer(string Name, LayerKey? Key, ImageLayerRole Role, List<ContourRegion> Regions, int Specks,
                                      int Snapped, List<PathD> Strokes, double StrokeWidth);

    /// <summary>Everything read from the picture before the scale is known.</summary>
    internal sealed record Prepared(ColourClusterSet Clusters, ImageLayerMap Map, List<PixelLayer> Layers,
                                    Dictionary<string, CoverageMap> Coverage, List<PixelPad> Pads, List<CircleFit> Drills,
                                    double NarrowestPx);

    internal static Prepared Prepare(ImageTraceInput input)
    {
        var tech = input.Technology!;
        var o = input.Options;
        var clusters = ColourClusters.Find(input.Source.Raster, Math.Max(1, o.MaxColours), new ColourClusterOptions { MergeDeltaE = o.MergeDeltaE });
        var map = input.LayerMap is { Edited: true } edited ? edited.Rebind(clusters) : ImageLayerMap.Auto(clusters, tech);
        var contour = new ContourOptions
        {
            Simplify = o.SimplifyPx, Snap = o.Snap, SnapAngleDeg = o.SnapAngleDeg, Snap45 = o.Snap45, MinArea = 0,
        };

        CoverageMap Sum(IEnumerable<int> ks)
        {
            int w = clusters.Width, h = clusters.Height;
            var v = new float[w * h];
            foreach (int k in ks)
            {
                var c = clusters.Coverage(k).Values;
                for (int i = 0; i < v.Length; i++) v[i] = Math.Min(1f, v[i] + c[i]);
            }
            if (input.Scope is { } s) ClipToScope(v, w, h, s);
            return new CoverageMap(w, h, v);
        }

        // Drills first: their circles are the holes the copper is not cut by.
        var drills = new List<CircleFit>();
        var drillRows = map.Rows.Where(r => r.Role == ImageLayerRole.Drill).Select(r => r.Cluster).ToList();
        if (drillRows.Count > 0)
            foreach (var region in Despeckle(Contours.Trace(Sum(drillRows), contour with { Snap = false }), o.MinFeaturePx2, out _))
                if (CircleOf(region) is { } fit) drills.Add(fit);

        var layers = new List<PixelLayer>();
        var coverage = new Dictionary<string, CoverageMap>(StringComparer.Ordinal);
        var byLayer = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var r in map.Rows.Where(r => r.Role == ImageLayerRole.Layer))
            foreach (string name in r.Layers)
                (byLayer.TryGetValue(name, out var l) ? l : byLayer[name] = []).Add(r.Cluster);

        // Copper in the technology's own order, top first; other layers after, by name.
        var copperOrder = ImageLayerMap.CopperLayers(tech).Select(k => ImageLayerMap.NameOf(tech, k)).ToList();
        var names = byLayer.Keys.OrderBy(n => copperOrder.IndexOf(n) is var i and >= 0 ? i : int.MaxValue).ThenBy(n => n, StringComparer.Ordinal);
        foreach (string name in names)
        {
            var cov = Sum(byLayer[name]);
            coverage[name] = cov;
            var regions = Deblip(Despeckle(Contours.Trace(cov, contour), o.MinFeaturePx2, out int specks));
            layers.Add(new PixelLayer(name, ImageLayerMap.KeyOf(tech, name), ImageLayerRole.Layer, regions, specks,
                                      o.Snap ? CountSnapped(regions, o.Snap45) : 0, [], 0));
        }

        foreach (var role in new[] { ImageLayerRole.Silkscreen, ImageLayerRole.BoardOutline })
        {
            var rows = map.Rows.Where(r => r.Role == role).ToList();
            if (rows.Count == 0) continue;
            var cov = Sum(rows.Select(r => r.Cluster));
            string name = rows.SelectMany(r => r.Layers).FirstOrDefault() ?? (role == ImageLayerRole.Silkscreen ? "silkscreen" : "outline");
            var regions = role == ImageLayerRole.Silkscreen ? Despeckle(Contours.Trace(cov, contour), o.MinFeaturePx2, out int sp) : [];
            var graph = SkeletonGraph.Build(cov.Threshold());
            layers.Add(new PixelLayer(name, rows.SelectMany(r => r.Layers).Select(n => ImageLayerMap.KeyOf(tech, n)).FirstOrDefault(),
                                      role, regions, 0, 0, [.. graph.Edges.Select(e => e.Polyline)], graph.StrokeWidth));
        }

        // Pads for the scale search: the rectangles on each copper layer.
        var pads = new List<PixelPad>();
        for (int li = 0; li < layers.Count; li++)
        {
            if (layers[li].Role != ImageLayerRole.Layer) continue;
            foreach (var region in layers[li].Regions)
                if (region.Holes.Count == 0 && RectOf(region.Outer) is { } r) pads.Add(new PixelPad(r.left, r.top, r.right, r.bottom, li + 1));
        }

        return new Prepared(clusters, map, layers, coverage, pads, drills, Narrowest(layers, copperOrder));
    }

    private static void ClipToScope(float[] v, int w, int h, PixelRect s)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x + 0.5 < s.Left || x + 0.5 > s.Right || y + 0.5 < s.Top || y + 0.5 > s.Bottom) v[y * w + x] = 0;
    }

    /// <summary>Regions and holes under the minimum feature removed, and counted.</summary>
    private static List<ContourRegion> Despeckle(List<ContourRegion> regions, double minArea, out int specks)
    {
        specks = 0;
        var kept = new List<ContourRegion>(regions.Count);
        foreach (var r in regions)
        {
            if (Math.Abs(Clipper.Area(r.Outer)) < minArea) { specks++; continue; }
            var holes = r.Holes.Where(hp => Math.Abs(Clipper.Area(hp)) >= minArea).ToList();
            specks += r.Holes.Count - holes.Count;
            kept.Add(holes.Count == r.Holes.Count ? r : r with { Holes = holes });
        }
        return kept;
    }

    /// <summary>An excursion off a snapped edge no further than this, and no longer than <see cref="BlipLengthPx"/>, is a
    /// blip.</summary>
    internal const double BlipPx = 0.5, BlipLengthPx = 4.0;

    /// <summary>
    /// Removes the blips a colour junction leaves on a straight edge. Where three or four colours meet in one pixel — a
    /// bottom layer's corner on a top layer's edge — the pixel is no two-colour mix, and the edge picks up an excursion
    /// of a fraction of a pixel that splits one snapped edge into two, each snapped about its own centroid a hair apart.
    /// Two edges on one snap direction whose lines are within <see cref="BlipPx"/>, joined by a few short edges that stay
    /// within it, are one edge: both are moved onto their length-weighted common line (their outer ends sliding along
    /// the edges before and after, so those keep their directions) and the excursion is dropped.
    /// </summary>
    private static List<ContourRegion> Deblip(List<ContourRegion> regions) =>
        [.. regions.Select(r => r with { Outer = Deblip(r.Outer), Holes = [.. r.Holes.Select(Deblip).Where(h => h.Count >= 3)] })];

    internal static PathD Deblip(PathD ring)
    {
        var p = new PathD(ring);
        for (bool changed = true; changed;)
        {
            changed = false;
            int n = p.Count;
            for (int i = 0; i < n && !changed; i++)
                for (int g = 2; g <= 5 && g + 3 <= n && !changed; g++)
                {
                    var a0 = p[i];
                    var a1 = p[(i + 1) % n];
                    var b0 = p[(i + g) % n];
                    var b1 = p[(i + g + 1) % n];
                    if (SnapDirection(a0, a1) is not { } d || SnapDirection(b0, b1) is not { } e || d != e) continue;
                    double ca = Cross(d, a0), cb = Cross(d, b0);
                    if (Math.Abs(ca - cb) > BlipPx) continue;
                    double run = 0;
                    bool within = true;
                    for (int k = 1; k < g && within; k++)
                    {
                        var v = p[(i + k + 1) % n];
                        within = Math.Min(Math.Abs(Cross(d, v) - ca), Math.Abs(Cross(d, v) - cb)) <= BlipPx;
                    }
                    for (int k = 1; k < g; k++) run += Dist(p[(i + k) % n], p[(i + k + 1) % n]);
                    if (!within || run > BlipLengthPx) continue;

                    double la = Dist(a0, a1), lb = Dist(b0, b1);
                    double c = (ca * la + cb * lb) / (la + lb);
                    var before = p[(i - 1 + n) % n];
                    var after = p[(i + g + 2) % n];
                    if (OnLine(d, c, before, a0) is not { } na || OnLine(d, c, after, b1) is not { } nb) continue;

                    var drop = new HashSet<int>();
                    for (int k = 1; k <= g; k++) drop.Add((i + k) % n);
                    var q = new PathD(n - g);
                    for (int k = 0; k < n; k++)
                    {
                        if (drop.Contains(k)) continue;
                        q.Add(k == i ? na : k == (i + g + 1) % n ? nb : p[k]);
                    }
                    p = q;
                    changed = true;
                }
        }
        return p;
    }

    /// <summary>The unit direction of an edge lying exactly on 0°, 90° or ±45° (up to sign), or null.</summary>
    private static (double X, double Y)? SnapDirection(PointD a, PointD b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len = Math.Max(Math.Abs(dx), Math.Abs(dy)), tol = 1e-9 * Math.Max(1, len);
        if (len <= 0) return null;
        if (Math.Abs(dy) <= tol) return (1, 0);
        if (Math.Abs(dx) <= tol) return (0, 1);
        if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= tol) return Math.Sign(dx) == Math.Sign(dy) ? (Math.Sqrt(0.5), Math.Sqrt(0.5)) : (Math.Sqrt(0.5), -Math.Sqrt(0.5));
        return null;
    }

    /// <summary>The offset of the line of direction <paramref name="d"/> through <paramref name="p"/>.</summary>
    private static double Cross((double X, double Y) d, PointD p) => d.X * p.y - d.Y * p.x;

    private static double Dist(PointD a, PointD b) => Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));

    /// <summary>Where the line through <paramref name="from"/> and <paramref name="to"/> meets the line of direction
    /// <paramref name="d"/> and offset <paramref name="c"/>; null when they are near parallel.</summary>
    private static PointD? OnLine((double X, double Y) d, double c, PointD from, PointD to)
    {
        double ex = to.x - from.x, ey = to.y - from.y, den = d.X * ey - d.Y * ex;
        if (Math.Abs(den) < 1e-9 * Math.Max(1, Math.Sqrt(ex * ex + ey * ey))) return null;
        double t = (c - Cross(d, from)) / den;
        return new PointD(from.x + t * ex, from.y + t * ey);
    }

    /// <summary>Edges lying exactly on a snap angle — what snapping produced.</summary>
    private static int CountSnapped(List<ContourRegion> regions, bool snap45)
    {
        int n = 0;
        foreach (var r in regions)
            foreach (var p in r.Paths)
                for (int i = 0; i < p.Count; i++)
                {
                    var a = p[i];
                    var b = p[(i + 1) % p.Count];
                    double dx = Math.Abs(b.x - a.x), dy = Math.Abs(b.y - a.y), len = Math.Max(dx, dy);
                    if (len <= 0) continue;
                    if (dx == 0 || dy == 0 || (snap45 && Math.Abs(dx - dy) <= 1e-7 * len)) n++;
                }
        return n;
    }

    /// <summary>A region that is a circle: a ring of at least eight vertices on one circle, enclosing its area.</summary>
    internal static CircleFit? CircleOf(ContourRegion region)
    {
        var outer = region.Outer;
        if (outer.Count < 8) return null;
        var fit = Fit.Circle(outer);
        if (!(fit.R > 1) || fit.Residual > Math.Max(0.35, 0.04 * fit.R)) return null;
        double area = Math.Abs(Clipper.Area(outer)), disc = Math.PI * fit.R * fit.R;
        return Math.Abs(area / disc - 1) <= 0.06 ? fit : null;
    }

    /// <summary>The bounds of a ring that is an axis-aligned rectangle, or null.</summary>
    internal static RectD? RectOf(PathD ring)
    {
        if (ring.Count != 4) return null;
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            if (Math.Abs(a.x - b.x) > 1e-6 && Math.Abs(a.y - b.y) > 1e-6) return null;
        }
        return Clipper.GetBounds(ring);
    }

    /// <summary>The narrowest traced copper line, pixels: the thinnest skeleton edge at least three widths long, else
    /// the narrower side of the smallest region.</summary>
    private static double Narrowest(List<PixelLayer> layers, List<string> copper)
    {
        double best = double.MaxValue;
        foreach (var l in layers.Where(l => l.Role == ImageLayerRole.Layer && copper.Contains(l.Name)))
            foreach (var r in l.Regions)
            {
                var b = Clipper.GetBounds(r.Outer);
                best = Math.Min(best, Math.Min(b.Width, b.Height));
                // A strip of length L and width W: L·W = area, L + W = half the perimeter. A shape no strip fits (a
                // disc) has no real root and is not a line.
                double area = Math.Abs(r.Area), half = Perimeter(r.Outer) / 2, disc = half * half - 4 * area;
                if (disc < 0) continue;
                double w = (half - Math.Sqrt(disc)) / 2;
                if (w > 0 && half - w >= 3 * w) best = Math.Min(best, w);
            }
        return best == double.MaxValue ? 0 : best;
    }

    private static double Perimeter(PathD p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        }
        return s;
    }

    // ── stage 2: DBU ────────────────────────────────────────────────────────────────────────────────

    private sealed record Built(PixelFrame Frame, int DbuPerMicron, List<ImageTracedLayer> Layers, List<LayoutShape> Shapes,
                                BitmapShape? Underlay, List<long[]> Strokes, string Resolution);

    private static Built Build(ImageTraceInput input, Prepared prep, ImageScaleCandidate scale, RecognitionReport report)
    {
        var tech = input.Technology!;
        var raster = input.Source.Raster;
        int w = raster.Width, h = raster.Height;
        var placement = input.Source.Placement as LayoutBitmapPlacement;

        // ── the frame: on the placed bitmap's rect, or with the picture's top-left at the origin ──────────
        PixelFrame frame;
        int dbuPerMicron = input.DbuPerMicron;
        if (scale is { Kind: ImageScaleKind.Placement, DbuPerPixel: var (sx, sy) } && placement is not null)
        {
            dbuPerMicron = scale.DbuPerMicron ?? dbuPerMicron;
            frame = new PixelFrame(sx, sy, placement.X, placement.Y + placement.H, true);
            if (Math.Abs(sx / sy - 1) > ImageScale.AspectTolerance)
                report.Add(RecognitionFindingClass.ImageBitmapStretched, 1,
                    $"The placed bitmap is stretched ({(sx / sy - 1) * 100:+0.#;-0.#} % across against down); the trace follows its rect.");
        }
        else
        {
            if (input.TargetIsPlacedLayout && placement is not null)
                dbuPerMicron = LayoutPersistence.TryReadUnits(placement.Document)?.DbuPerMicron ?? dbuPerMicron;
            double s = scale.MetresPerPixel * 1e6 * dbuPerMicron;
            double ox = input.TargetIsPlacedLayout && placement is not null ? placement.X : 0;
            double oy = input.TargetIsPlacedLayout && placement is not null ? placement.Y + placement.H : 0;
            frame = new PixelFrame(s, s, ox, oy, true);
        }
        double scaleDbu = 0.5 * (frame.ScaleX + frame.ScaleY);
        double techToLayout = dbuPerMicron / (double)LayoutUnits.DefaultDbuPerMicron;

        var copperOrder = ImageLayerMap.CopperLayers(tech);
        var shapes = new List<LayoutShape>();
        var traced = new List<ImageTracedLayer>();
        var consumed = new HashSet<ContourRegion>();

        // ── drills → vias ──────────────────────────────────────────────────────────────────────────
        var barrel = ImageLayerMap.ViaBarrelLayer(tech);
        var vias = new List<LayoutShape>();
        foreach (var d in prep.Drills)
        {
            if (barrel is null) continue;
            LayerKey? landing = null;
            long pad = 0;
            foreach (var l in prep.Layers.Where(l => l.Role == ImageLayerRole.Layer && l.Key is { } k && copperOrder.Contains(k))
                                         .OrderBy(l => copperOrder.IndexOf(l.Key!.Value)))
            {
                var holder = l.Regions.FirstOrDefault(r => Clipper.PointInPolygon(new PointD(d.Cx, d.Cy), r.Outer) != PointInPolygonResult.IsOutside);
                if (holder is null) continue;
                landing ??= l.Key;
                if (pad == 0 && holder.Holes.Count <= 1 && CircleOf(holder with { Holes = [] }) is { } ring
                    && Math.Abs(ring.Cx - d.Cx) <= 0.5 * d.R && Math.Abs(ring.Cy - d.Cy) <= 0.5 * d.R && ring.R > d.R)
                {
                    pad = (long)Math.Round(2 * ring.R * scaleDbu);
                    consumed.Add(holder);
                }
            }
            long drill = (long)Math.Round(2 * d.R * scaleDbu);
            if (pad <= drill)
            {
                long fallback = (long)Math.Round(tech.DefaultViaPadDbu * techToLayout);
                pad = fallback > drill ? fallback : 2 * drill;
            }
            var (cx, cy) = frame.ToTarget(d.Cx, d.Cy);
            vias.Add(new ViaShape
            {
                X = (long)Math.Round(cx), Y = (long)Math.Round(cy), PadSize = pad, DrillSize = drill,
                Layer = barrel.Value, LandingLayer = landing ?? (copperOrder.Count > 0 ? copperOrder[0] : null),
            });
        }
        if (prep.Drills.Count > 0)
        {
            if (barrel is null)
                report.Add(RecognitionFindingClass.ImageDrillNoViaLayer, prep.Drills.Count,
                    $"{Plural(prep.Drills.Count, "drill hole was", "drill holes were")} read, but {tech.Name} has no via layer, " +
                    "so they were written on no layer.");
            else
                report.Add(RecognitionFindingClass.ImageViasPlaced, vias.Count,
                    $"{Plural(vias.Count, "drill circle became a via", "drill circles became vias")} on {ImageLayerMap.NameOf(tech, barrel.Value)}.");
        }

        // ── each layer ─────────────────────────────────────────────────────────────────────────────
        int circles = 0;
        var strokes = new List<long[]>();
        foreach (var l in prep.Layers)
        {
            var these = new List<LayoutShape>();
            if (l.Key is { } key)
            {
                foreach (var region in l.Regions)
                {
                    if (consumed.Contains(region)) continue;
                    var holes = region.Holes.Where(hp => !prep.Drills.Any(d => Within(hp, d))).ToList();
                    if (holes.Count == 0 && CircleOf(region) is { } c)
                    {
                        var (cx, cy) = frame.ToTarget(c.Cx, c.Cy);
                        these.Add(new CircleShape { Layer = key, Cx = (long)Math.Round(cx), Cy = (long)Math.Round(cy), R = (long)Math.Round(c.R * scaleDbu) });
                        circles++;
                        continue;
                    }
                    these.Add(ShapeOf(region.Outer, holes, frame, key));
                }
                if (l.Role == ImageLayerRole.BoardOutline || l.Role == ImageLayerRole.Silkscreen)
                {
                    long width = Math.Max(1, (long)Math.Round(Math.Max(1, l.StrokeWidth) * scaleDbu));
                    foreach (var stroke in l.Strokes)
                    {
                        var xy = Quantise(frame.ToTarget(stroke), closed: false);
                        if (l.Role == ImageLayerRole.Silkscreen) strokes.Add(xy);
                        else if (xy.Length >= 4) these.Add(new PathShape { Layer = key, Xy = xy, Width = width });
                    }
                }
            }
            else if (l.Role == ImageLayerRole.Silkscreen)
                foreach (var stroke in l.Strokes) strokes.Add(Quantise(frame.ToTarget(stroke), closed: false));

            traced.Add(new ImageTracedLayer(l.Name, l.Key, l.Role, l.Regions, these));
            shapes.AddRange(these);
        }
        shapes.AddRange(vias);

        // ── the underlay (D5) ──────────────────────────────────────────────────────────────────────
        BitmapShape? underlay = null;
        bool alreadyThere = input.TargetIsPlacedLayout && placement is not null;
        if (input.Options.KeepUnderlay && !alreadyThere)
        {
            var doc = ImageLayerMap.DocumentationLayer(tech);
            var layer = doc ?? traced.Where(t => t.Key is { } k && copperOrder.Contains(k))
                                     .OrderByDescending(t => t.PixelRegions.Sum(r => Math.Abs(r.Area))).Select(t => t.Key).FirstOrDefault()
                             ?? (copperOrder.Count > 0 ? copperOrder[0] : default);
            var (x0, yTop) = frame.ToTarget(0, 0);
            var (x1, yBottom) = frame.ToTarget(w, h);
            underlay = new BitmapShape
            {
                Layer = layer, ImagePathRef = input.Source.Path ?? "",
                X = (long)Math.Round(x0), Y = (long)Math.Round(yBottom),
                W = (long)Math.Round(x1 - x0), H = (long)Math.Round(yTop - yBottom),
                Opacity = UnderlayOpacity, Locked = true,
            };
            if (doc is null)
                report.Add(RecognitionFindingClass.ImageUnderlayOnCopper, 1,
                    $"{tech.Name} has no documentation layer, so the source picture lies on {ImageLayerMap.NameOf(tech, layer)}: " +
                    "hiding that layer hides the picture.");
        }

        // ── the report ─────────────────────────────────────────────────────────────────────────────
        report.Add(RecognitionFindingClass.ImageScaleChosen, 1, $"Scale {scale.Display} — {scale.Evidence}.");
        string resolution = ImageScale.ResolutionLine(scale.MetresPerPixel, prep.NarrowestPx);
        report.Add(RecognitionFindingClass.ImageResolution, 1, resolution + ".");
        var perLayer = traced.Where(t => t.Shapes.Count > 0).Select(t => $"{t.Name}: {t.Shapes.Count}").ToList();
        report.Add(RecognitionFindingClass.ImageShapesTraced, shapes.Count,
            $"{Plural(shapes.Count, "shape", "shapes")} traced — {string.Join("; ", perLayer)}" + (vias.Count > 0 ? $"; vias: {vias.Count}" : "") + ".");
        report.Add(RecognitionFindingClass.ImageCirclesFitted, circles, $"{Plural(circles, "region was fitted as a circle", "regions were fitted as circles")}.");
        ReportPixels(prep, report);
        if (strokes.Count > 0)
            report.Add(RecognitionFindingClass.ImageSilkscreenStrokes, strokes.Count,
                $"{Plural(strokes.Count, "silkscreen stroke was", "silkscreen strokes were")} kept as centre lines for reading text.");

        return new Built(frame, dbuPerMicron, traced, shapes, underlay, strokes, resolution);
    }

    private static bool Within(PathD hole, CircleFit d)
    {
        var b = Clipper.GetBounds(hole);
        double cx = (b.left + b.right) / 2, cy = (b.top + b.bottom) / 2;
        return (cx - d.Cx) * (cx - d.Cx) + (cy - d.Cy) * (cy - d.Cy) <= (d.R + 1) * (d.R + 1);
    }

    /// <summary>A region as a shape: an axis-aligned rectangle as a <see cref="RectShape"/>, anything else as a polygon
    /// with its holes.</summary>
    private static LayoutShape ShapeOf(PathD outer, List<PathD> holes, PixelFrame frame, LayerKey layer)
    {
        long[] xy = Quantise(frame.ToTarget(outer), closed: true);
        if (holes.Count == 0 && xy.Length == 8 && IsAxisRect(xy))
            return new RectShape
            {
                Layer = layer, X1 = Math.Min(xy[0], xy[4]), Y1 = Math.Min(xy[1], xy[5]), X2 = Math.Max(xy[0], xy[4]), Y2 = Math.Max(xy[1], xy[5]),
            };
        return new PolygonShape
        {
            Layer = layer, Xy = xy,
            Holes = holes.Count == 0 ? null : [.. holes.Select(hp => Quantise(frame.ToTarget(hp), closed: true))],
        };
    }

    private static bool IsAxisRect(long[] xy)
    {
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            if (xy[2 * i] != xy[2 * j] && xy[2 * i + 1] != xy[2 * j + 1]) return false;
        }
        return true;
    }

    private enum EdgeKind { Free, H, V, DPlus, DMinus }

    /// <summary>
    /// Rounds a path to DBU so every snapped edge stays snapped: each edge's invariant — y for a horizontal edge, x for a
    /// vertical one, x − y or x + y for a 45° one — is rounded ONCE, and each vertex is solved from the invariants of the
    /// two edges meeting at it. Rounding x and y per vertex would leave a 45° jog a DBU off 45°.
    /// </summary>
    internal static long[] Quantise(PathD p, bool closed)
    {
        int n = p.Count;
        var result = new long[2 * n];
        if (n == 0) return result;
        int edges = closed ? n : n - 1;
        var kind = new EdgeKind[Math.Max(0, edges)];
        var inv = new long[Math.Max(0, edges)];
        for (int e = 0; e < edges; e++)
        {
            var a = p[e];
            var b = p[(e + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Max(Math.Abs(dx), Math.Abs(dy)), tol = 1e-7 * Math.Max(len, 1);
            if (Math.Abs(dy) <= tol && Math.Abs(dx) > tol) { kind[e] = EdgeKind.H; inv[e] = (long)Math.Round((a.y + b.y) / 2); }
            else if (Math.Abs(dx) <= tol && Math.Abs(dy) > tol) { kind[e] = EdgeKind.V; inv[e] = (long)Math.Round((a.x + b.x) / 2); }
            else if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= tol && len > tol)
            {
                bool plus = Math.Sign(dx) == Math.Sign(dy);
                kind[e] = plus ? EdgeKind.DPlus : EdgeKind.DMinus;
                inv[e] = (long)Math.Round(plus ? ((a.x - a.y) + (b.x - b.y)) / 2 : ((a.x + a.y) + (b.x + b.y)) / 2);
            }
        }
        for (int i = 0; i < n; i++)
        {
            int eIn = closed ? (i - 1 + n) % n : i - 1, eOut = closed || i < n - 1 ? i : -1;
            long? x = null, y = null;
            void Apply(int e)
            {
                if (e < 0 || e >= edges) return;
                switch (kind[e])
                {
                    case EdgeKind.H: y ??= inv[e]; break;
                    case EdgeKind.V: x ??= inv[e]; break;
                }
            }
            Apply(eIn);
            Apply(eOut);
            void Diagonal(int e)
            {
                if (e < 0 || e >= edges || kind[e] is not (EdgeKind.DPlus or EdgeKind.DMinus)) return;
                bool plus = kind[e] == EdgeKind.DPlus;
                if (x is { } xv && y is null) y = plus ? xv - inv[e] : inv[e] - xv;
                else if (y is { } yv && x is null) x = plus ? inv[e] + yv : inv[e] - yv;
            }
            Diagonal(eIn);
            Diagonal(eOut);
            if (x is null && y is null && eIn >= 0 && eOut >= 0 && eIn < edges && eOut < edges
                && kind[eIn] is EdgeKind.DPlus or EdgeKind.DMinus && kind[eOut] is EdgeKind.DPlus or EdgeKind.DMinus && kind[eIn] != kind[eOut])
            {
                long cp = kind[eIn] == EdgeKind.DPlus ? inv[eIn] : inv[eOut], cm = kind[eIn] == EdgeKind.DMinus ? inv[eIn] : inv[eOut];
                x = (long)Math.Round((cp + cm) / 2.0);
                y = cm - x;
            }
            if (x is null && y is null)
            {
                x = (long)Math.Round(p[i].x);
                Diagonal(eIn);
                Diagonal(eOut);
            }
            result[2 * i] = x ?? (long)Math.Round(p[i].x);
            result[2 * i + 1] = y ?? (long)Math.Round(p[i].y);
        }
        return result;
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────

    private static void ReportColours(Prepared prep, RecognitionReport report)
    {
        var rows = prep.Map.Rows;
        report.Add(RecognitionFindingClass.ImageColoursMapped, rows.Count,
            $"{Plural(rows.Count, "colour", "colours")}: " +
            string.Join(", ", rows.Select(r => $"{r.Hex} → {(r.Overlap ? r.Spelling + " (overlap)" : r.Spelling)} ({r.Share * 100:0.#} %)")) + ".");
        var ignored = rows.Where(r => r.Role == ImageLayerRole.Ignore || (r.Role != ImageLayerRole.Background && r.Layers.Count == 0 && r.Role != ImageLayerRole.Drill)).ToList();
        report.Add(RecognitionFindingClass.ImageIgnored, ignored.Count,
            $"{Plural(ignored.Count, "colour was", "colours were")} not traced: {string.Join(", ", ignored.Select(r => $"{r.Hex} ({r.Share * 100:0.#} %)"))}.");
    }

    private static void ReportPixels(Prepared prep, RecognitionReport report)
    {
        int specks = prep.Layers.Sum(l => l.Specks), snapped = prep.Layers.Sum(l => l.Snapped);
        report.Add(RecognitionFindingClass.ImageSpecksRemoved, specks,
            $"{Plural(specks, "speck", "specks")} below the minimum feature {(specks == 1 ? "was" : "were")} removed.");
        report.Add(RecognitionFindingClass.ImageEdgesSnapped, snapped,
            $"{Plural(snapped, "edge lies", "edges lie")} on 0°, 45° or 90°.");
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
