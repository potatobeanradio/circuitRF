// How big a pixel is — brief-img-3-trace-layout-image.md R-im3-2, R-im3-3 (D6).
//
// A picture has no millimetres in it, and a wrong scale is off by orders of magnitude while looking fine — so the scale
// is never a silent guess. Every piece of evidence found is listed with its kind, its metres per pixel and its support;
// Choose takes, in order:
//   1. the placed bitmap's own rect, when the target is the layout it sits in;
//   2. a scale the user stated — a number, two picked points and a distance with a unit, or a trace of stated Z0;
//   3. chip land patterns agreeing at one scale, at least three pairs within 3 %;
//   4. otherwise NO scale: a refusal for a write, a waiting state in the dialog.
// The file's resolution metadata is listed and never chosen: a screenshot's 96 dpi describes a screen.

using System.Globalization;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>Where a scale came from.</summary>
public enum ImageScaleKind { Placement, Stated, TwoPoints, Impedance, Parts, Resolution }

/// <summary>A point in the picture, pixels (x right, y down, pixel centres at ½).</summary>
public readonly record struct PixelPoint(double X, double Y);

/// <summary>What the user said about the scale.</summary>
public sealed record ImageScaleStatement
{
    public enum StatementKind { Auto, Stated, TwoPoints, Impedance }

    public StatementKind Kind { get; init; }

    /// <summary>A stated scale, metres per pixel.</summary>
    public double MetresPerPixel { get; init; }

    /// <summary>Two picked points and the distance between them, with its unit (<c>1.6 mm</c>, <c>62 mil</c>).</summary>
    public PixelPoint A { get; init; }
    public PixelPoint B { get; init; }
    public string Distance { get; init; } = "";

    /// <summary>A picked point on a trace, its impedance, and the layer it is on (null: the layer its colour maps to).</summary>
    public PixelPoint Point { get; init; }
    public double Z0 { get; init; }
    public string? Layer { get; init; }

    /// <summary>No statement: the evidence decides (D6).</summary>
    public static ImageScaleStatement Auto { get; } = new();

    public static ImageScaleStatement Stated(double metresPerPixel) => new() { Kind = StatementKind.Stated, MetresPerPixel = metresPerPixel };

    public static ImageScaleStatement TwoPoints(PixelPoint a, PixelPoint b, string distance) =>
        new() { Kind = StatementKind.TwoPoints, A = a, B = b, Distance = distance };

    public static ImageScaleStatement Impedance(PixelPoint onTrace, double z0, string? layer = null) =>
        new() { Kind = StatementKind.Impedance, Point = onTrace, Z0 = z0, Layer = layer };
}

/// <summary>One piece of scale evidence.</summary>
/// <param name="Support">How strongly it is held: the pairs that agree, for <see cref="ImageScaleKind.Parts"/>; 1 otherwise.</param>
/// <param name="Evidence">The evidence in words — what the provenance and the report carry.</param>
public sealed record ImageScaleCandidate(ImageScaleKind Kind, double MetresPerPixel, int Support, string Evidence)
{
    /// <summary>Offered, never chosen (D6 (5)).</summary>
    public bool OfferedOnly => Kind == ImageScaleKind.Resolution;

    /// <summary>For a placement: the bitmap's rect per pixel along x and y, DBU — what lays the trace exactly on it.</summary>
    public (double X, double Y)? DbuPerPixel { get; init; }

    /// <summary>For a placement: the layout's DBU per micron.</summary>
    public int? DbuPerMicron { get; init; }

    /// <summary><c>25.4 µm/px</c>.</summary>
    public string Display => ImageScale.FormatLength(MetresPerPixel) + "/px";
}

public static class ImageScale
{
    /// <summary>Parts evidence is inferred only when at least this many pairs agree (D6 (3)).</summary>
    public const int MinPartsSupport = 3;

    /// <summary>A placed bitmap whose two axes disagree by more than this was stretched.</summary>
    public const double AspectTolerance = 0.01;

    /// <summary>Every piece of scale evidence in <paramref name="input"/>'s picture.</summary>
    public static IReadOnlyList<ImageScaleCandidate> Candidates(ImageTraceInput input) =>
        input.Technology is null ? [] : Candidates(input, ImageTrace.Prepare(input), out _);

    internal static IReadOnlyList<ImageScaleCandidate> Candidates(ImageTraceInput input, ImageTrace.Prepared prepared, out string? refusal)
    {
        refusal = null;
        var list = new List<ImageScaleCandidate>();
        var raster = input.Source.Raster;

        if (input.Source.Placement is LayoutBitmapPlacement p && FromPlacement(p, raster.Width, raster.Height) is { } placed)
            list.Add(placed);

        var st = input.Scale;
        switch (st.Kind)
        {
            case ImageScaleStatement.StatementKind.Stated:
                if (!(st.MetresPerPixel > 0) || !double.IsFinite(st.MetresPerPixel)) refusal = "A stated scale must be a positive length per pixel.";
                else list.Add(new ImageScaleCandidate(ImageScaleKind.Stated, st.MetresPerPixel, 1, $"stated as {FormatLength(st.MetresPerPixel)} per pixel"));
                break;
            case ImageScaleStatement.StatementKind.TwoPoints:
                if (FromTwoPoints(st.A, st.B, st.Distance, out string? why) is { } two) list.Add(two);
                else refusal = why;
                break;
            case ImageScaleStatement.StatementKind.Impedance:
                if (FromImpedance(input, prepared, st, out string? zwhy) is { } z) list.Add(z);
                else refusal = zwhy;
                break;
        }

        foreach (var s in LandPatternMatch.ScaleSearch(prepared.Pads))
            list.Add(new ImageScaleCandidate(ImageScaleKind.Parts, s.UmPerPixel * 1e-6, s.Support,
                $"{s.Support} land pattern{(s.Support == 1 ? "" : "s")} agree ({s.CasesText})"));

        if (raster.StatedResolution is { } res && res.XDpi > 0)
        {
            double m = 0.0254 / res.XDpi / raster.ReductionFactor;
            list.Add(new ImageScaleCandidate(ImageScaleKind.Resolution, m, 0,
                $"the file states {res.XDpi.ToString("0.#", CultureInfo.InvariantCulture)} dpi ({res.Source}) — offered, not used"));
        }
        return list;
    }

    /// <summary>D6's choice, or null — no scale.</summary>
    /// <param name="targetIsPlacedLayout">The trace goes into the layout the picture's bitmap sits in.</param>
    public static ImageScaleCandidate? Choose(IReadOnlyList<ImageScaleCandidate> candidates, bool targetIsPlacedLayout)
    {
        if (targetIsPlacedLayout && candidates.FirstOrDefault(c => c.Kind == ImageScaleKind.Placement) is { } placed) return placed;
        if (candidates.FirstOrDefault(c => c.Kind is ImageScaleKind.Stated or ImageScaleKind.TwoPoints or ImageScaleKind.Impedance) is { } stated)
            return stated;
        return candidates.Where(c => c.Kind == ImageScaleKind.Parts && c.Support >= MinPartsSupport)
                         .OrderByDescending(c => c.Support).FirstOrDefault();
    }

    /// <summary>A placed bitmap's rect over its pixel size. The layout's DBU per micron is read from its file.</summary>
    public static ImageScaleCandidate? FromPlacement(LayoutBitmapPlacement p, int widthPx, int heightPx)
    {
        if (p.W <= 0 || p.H <= 0) return null;
        int dbu = LayoutPersistence.TryReadUnits(p.Document)?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        double sx = (double)p.W / widthPx, sy = (double)p.H / heightPx;
        double m = sx / dbu * 1e-6;
        string evidence = $"the placed bitmap's size, {FormatLength(p.W / (double)dbu * 1e-6)} across {widthPx} px";
        if (Math.Abs(sx / sy - 1) > AspectTolerance)
            evidence += $"; it is stretched — {FormatLength(sy / dbu * 1e-6)} per pixel down against {FormatLength(m)} across";
        return new ImageScaleCandidate(ImageScaleKind.Placement, m, 1, evidence) { DbuPerPixel = (sx, sy), DbuPerMicron = dbu };
    }

    /// <summary>Two picked points and the distance between them. A bare number is refused — it could be any unit.</summary>
    public static ImageScaleCandidate? FromTwoPoints(PixelPoint a, PixelPoint b, string distance, out string? refusal)
    {
        refusal = null;
        string text = (distance ?? "").Trim();
        double px = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (!(px > 0)) { refusal = "The two points are the same point; pick two points apart."; return null; }
        if (text.Length == 0 || !char.IsLetter(text[^1]))
        {
            refusal = $"The distance '{text}' has no unit, and it could be micrometres, millimetres or mils. " +
                      $"Give it one: '{text} mm', '{text} um' or '{text} mil'.";
            return null;
        }
        if (!LayoutUnits.TryParse(text, LayoutUnit.Um, 1000, out long nm) || nm <= 0)
        {
            refusal = $"'{text}' is not a distance circuitRF reads (a number with nm, um, mm, mil or in).";
            return null;
        }
        double m = nm * 1e-9 / px;
        return new ImageScaleCandidate(ImageScaleKind.TwoPoints, m, 1,
            $"two points {px.ToString("0.#", CultureInfo.InvariantCulture)} px apart stated as {text}");
    }

    /// <summary>A trace of stated Z0: the technology gives its width, the picture its width in pixels.</summary>
    private static ImageScaleCandidate? FromImpedance(ImageTraceInput input, ImageTrace.Prepared prepared, ImageScaleStatement st,
                                                      out string? refusal)
    {
        refusal = null;
        var tech = input.Technology!;
        int x = (int)Math.Floor(st.Point.X), y = (int)Math.Floor(st.Point.Y);
        var raster = input.Source.Raster;
        if ((uint)x >= (uint)raster.Width || (uint)y >= (uint)raster.Height) { refusal = "The picked point is outside the picture."; return null; }
        string? layer = st.Layer;
        if (layer is null)
        {
            var row = prepared.Map.Of(prepared.Clusters.Label(x, y));
            layer = row is { Role: ImageLayerRole.Layer, Layers.Count: > 0 } ? row.Layers[0] : null;
        }
        if (layer is null || !prepared.Coverage.TryGetValue(layer, out var cov))
        {
            refusal = "The picked point is not on a traced layer; click on the trace itself.";
            return null;
        }
        double widthPx = LocalWidth(cov, x, y);
        if (!(widthPx > 0.5)) { refusal = "The picked point is not on a trace; click on the trace itself."; return null; }

        var calc = LineCalculator.Calculate(tech, new LineCalcRequest(layer, [], [st.Z0]));
        if (!calc.Ok) { refusal = $"The technology cannot give a width for {st.Z0:0.##} Ω on {layer}: {calc.Refusal}"; return null; }
        var r = calc.Rows.FirstOrDefault();
        double? w = r?.Model?.WidthM ?? r?.CrossSection?.WidthM;
        if (w is not > 0) { refusal = $"The technology gives no width for {st.Z0:0.##} Ω on {layer}: {r?.ModelRefusal ?? "out of range"}."; return null; }
        return new ImageScaleCandidate(ImageScaleKind.Impedance, w.Value / widthPx, 1,
            $"a {st.Z0.ToString("0.##", CultureInfo.InvariantCulture)} Ω trace on {layer} is {FormatLength(w.Value)} wide and " +
            $"{widthPx.ToString("0.0", CultureInfo.InvariantCulture)} px in the picture");
    }

    /// <summary>The width of the strip through (x, y), from the coverage summed along its row (h) and its column (v):
    /// a straight strip of width W at any angle has <c>W = h·v / √(h² + v²)</c>.</summary>
    internal static double LocalWidth(CoverageMap cov, int x, int y)
    {
        if (cov[x, y] < 0.5f) return 0;
        double Run(int dx, int dy)
        {
            double sum = 0;
            for (int i = 1; ; i++)
            {
                int xx = x + i * dx, yy = y + i * dy;
                if ((uint)xx >= (uint)cov.Width || (uint)yy >= (uint)cov.Height) break;
                float c = cov[xx, yy];
                if (c <= 0.02f) break;
                sum += c;
            }
            return sum;
        }
        double h = cov[x, y] + Run(1, 0) + Run(-1, 0), v = cov[x, y] + Run(0, 1) + Run(0, -1);
        return h * v / Math.Sqrt(h * h + v * v);
    }

    /// <summary>A length in the unit that reads best, three significant figures: <c>25.4 µm</c>, <c>1.6 mm</c>.</summary>
    public static string FormatLength(double metres)
    {
        double a = Math.Abs(metres);
        var (v, u) = a >= 1 ? (metres, "m") : a >= 1e-3 ? (metres * 1e3, "mm") : a >= 1e-6 ? (metres * 1e6, "µm") : (metres * 1e9, "nm");
        return $"{Significant(v)} {u}";
    }

    internal static string Significant(double v)
    {
        if (v == 0) return "0";
        int digits = Math.Max(0, 2 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
        return Math.Round(v, digits).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>R-im3-3: <c>1 px = 25.4 µm; a 300 µm trace is read to ± 4 %</c> — the half-pixel edge accuracy as a
    /// width uncertainty at the narrowest traced line.</summary>
    public static string ResolutionLine(double metresPerPixel, double narrowestPx)
    {
        string head = $"1 px = {FormatLength(metresPerPixel)}";
        if (!(narrowestPx > 0)) return head;
        double pct = Math.Max(1, Math.Round(50.0 / narrowestPx));
        return $"{head}; a {FormatLength(narrowestPx * metresPerPixel)} trace is read to ± {pct.ToString("0", CultureInfo.InvariantCulture)} %";
    }
}
