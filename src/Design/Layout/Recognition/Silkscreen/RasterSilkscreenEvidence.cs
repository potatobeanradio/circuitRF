// Silkscreen read from a picture's pixels — brief-img-4-layout-image-to-schematic.md R-im4-4 (overview D10).
//
// A picture's legend has no pen to read. What IM-3 kept of it is its skeleton: the centre lines of the strokes, brought
// through the trace's PixelFrame into DBU — the form StrokeGlyphs reads a Gerber pen's strokes in. So this is a source of
// claims and nothing more, plugged in through IPartEvidenceSource: the AS-10 matcher, its designator rule, RefdesAssociation
// and Learn These Glyphs (through PartClaim.Line, which a corrected parts row teaches from) all apply unchanged.
//
// One repair on the way: a skeleton cuts a sharp corner — the "2"'s foot, the "Z"'s and "4"'s — by a pixel or two, which
// the font never does, and the matcher's letter gap is tight enough for that to split "C12" into "C1" and a stray "2"
// (the font sets "1" and "2" 0.57 cap heights apart against a limit of 0.6). Each corner sharper than 120° is put back
// where its two straight legs, fitted clear of the corner, meet; a corner already there is left where it is.
//
// A stroke under 1.5 px is not read. Its skeleton is the anti-aliasing's, not the letter's — a 1 px line's centre wanders by
// a fraction of its own width at every pixel — and a misread designator names the wrong part, which is worse than none.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>The designators on a traced picture's silkscreen, as part claims (R-im4-4).</summary>
public sealed class RasterSilkscreenEvidence : IPartEvidenceSource
{
    /// <summary>Strokes thinner than this, pixels, are too thin to read.</summary>
    public const double MinStrokePx = 1.5;

    private readonly IReadOnlyList<long[]> _strokes;
    private readonly double _widthDbu;
    private readonly bool _bottom;
    private GlyphTemplates? _templates;
    private (IReadOnlyList<SilkTextLine> Lines, int Unread)? _read;

    /// <param name="strokes">The centre lines, DBU, flat x,y.</param>
    /// <param name="strokeWidthPx">Their width as the skeleton measured it, pixels.</param>
    /// <param name="dbuPerPixel">The trace's scale.</param>
    /// <param name="layer">The silkscreen layer they were mapped to, or null.</param>
    /// <param name="bottom">They are the bottom side's, read mirrored first.</param>
    /// <param name="templates">The glyphs; null is the user's (built-in plus taught), as the layout reader uses.</param>
    public RasterSilkscreenEvidence(IReadOnlyList<long[]> strokes, double strokeWidthPx, double dbuPerPixel, LayerKey? layer,
                                    bool bottom = false, GlyphTemplates? templates = null)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        _strokes = strokes;
        StrokeWidthPx = strokeWidthPx;
        _widthDbu = Math.Max(1, strokeWidthPx * dbuPerPixel);
        Layer = layer;
        _bottom = bottom;
        _templates = templates;
    }

    /// <summary>The evidence a trace's silkscreen gives, or null when it kept no strokes (or has no scale).</summary>
    public static RasterSilkscreenEvidence? From(Image.ImageTraceResult trace, Technology? technology, GlyphTemplates? templates = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.SilkscreenStrokes.Count == 0 || trace.Frame is not { } frame) return null;
        bool bottom = technology is not null && trace.SilkscreenLayer is { } key
                      && SilkscreenText.Layers(technology).Any(l => l.Layer == key && l.Bottom);
        return new RasterSilkscreenEvidence(trace.SilkscreenStrokes, trace.SilkscreenStrokeWidthPx,
                                            0.5 * (frame.ScaleX + frame.ScaleY), trace.SilkscreenLayer, bottom, templates);
    }

    public PartEvidenceSource Source => PartEvidenceSource.Silkscreen;

    /// <summary>The strokes' width, pixels.</summary>
    public double StrokeWidthPx { get; }

    /// <summary>Under <see cref="MinStrokePx"/>: nothing is read.</summary>
    public bool TooThin => StrokeWidthPx < MinStrokePx;

    public LayerKey? Layer { get; }

    /// <summary>The lines of text read; empty when the strokes are too thin.</summary>
    public IReadOnlyList<SilkTextLine> Lines => TooThin ? [] : Read().Lines;

    public IEnumerable<PartClaim> Claims(RecognitionInput input, BoardGraph board) =>
        Lines.Where(l => l.Refdes is not null).Select(l => new PartClaim(Source, l.X, l.Y, l.Refdes!, 0) { Line = l });

    /// <summary>What was read, or why nothing was — one finding.</summary>
    public void Report(RecognitionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (TooThin)
        {
            report.Add(RecognitionFindingClass.ImageSilkscreenTooThin, _strokes.Count,
                $"The silkscreen's strokes are {StrokeWidthPx:0.#} px wide, too thin to read (at least {MinStrokePx:0.#} px), so no " +
                "designator was read from the picture.");
            return;
        }
        var lines = Lines;
        int designators = lines.Count(l => l.Refdes is not null), unsure = lines.Sum(StrokeGlyphs.LowMarginGlyphs);
        report.Add(RecognitionFindingClass.ImageSilkscreenRead, lines.Count,
            $"{lines.Count} line{(lines.Count == 1 ? "" : "s")} of text {(lines.Count == 1 ? "was" : "were")} read on the picture's " +
            $"silkscreen, {designators} of them designator{(designators == 1 ? "" : "s")}: " +
            $"{string.Join(", ", lines.Take(16).Select(l => l.Refdes ?? $"'{l.Text}'"))}{(lines.Count > 16 ? ", …" : "")}" +
            (unsure > 0 ? $"; {unsure} glyph{(unsure == 1 ? " fits" : "s fit")} two characters about as well." : "."),
            [.. lines.Select(l => new RecognitionAnchor(l.X, l.Y, Layer))]);
    }

    /// <summary>A corner this sharp or sharper (interior angle, degrees) is one the skeleton cuts.</summary>
    internal const double SharpCornerDeg = 120;

    /// <summary>
    /// The centre line simplified to half a pen, each sharp corner the skeleton cut put back (this file's header). A
    /// corner is a vertex of the simplified line — or a chamfer, a segment under 1.5 pens, taken as one — between two legs
    /// of two pens or more. Each leg's line is fitted to the original centre line clear of the corner by 1.5 pens, and the
    /// corner moves to where the two lines meet when they turn sharply and meet within two pens of it.
    /// </summary>
    internal static double[] RestoreCorners(double[] xy, double pen)
    {
        int n = xy.Length / 2;
        if (n < 3) return xy;
        var pts = new List<PointD>(n);
        for (int i = 0; i < n; i++) pts.Add(new PointD(xy[2 * i], xy[2 * i + 1]));
        var keep = Polylines.SimplifyOpen(pts, 0.5 * pen);
        var v = keep.Select(i => pts[i]).ToList();
        double sharp = Math.Cos(SharpCornerDeg * Math.PI / 180);
        for (int k = 1; k + 1 < v.Count; k++)
        {
            // The corner: vertex k, or the chamfer k … k+1.
            int last = k + 2 < v.Count && Dist(v[k], v[k + 1]) < 1.5 * pen ? k + 1 : k;
            if (Dist(v[k - 1], v[k]) < 2 * pen || Dist(v[last], v[last + 1]) < 2 * pen) continue;
            var centre = new PointD(0.5 * (v[k].x + v[last].x), 0.5 * (v[k].y + v[last].y));
            if (Leg(pts, keep[k - 1], keep[k], centre, 1.5 * pen) is not { } a
                || Leg(pts, keep[last], keep[last + 1], centre, 1.5 * pen) is not { } b
                || Meet(a.P, a.Q, b.P, b.Q) is not { } apex) continue;
            double cos = Cos(v[k - 1], apex, v[last + 1]);
            if (cos < sharp || Dist(apex, centre) > 2 * pen) continue;
            v[k] = apex;
            if (last > k) { v.RemoveAt(last); keep.RemoveAt(last); }
        }
        var flat = new double[2 * v.Count];
        for (int i = 0; i < v.Count; i++) (flat[2 * i], flat[2 * i + 1]) = (v[i].x, v[i].y);
        return flat;
    }

    /// <summary>The line through the original centre line from index <paramref name="from"/> to <paramref name="to"/>,
    /// resampled every tenth of the clearance and fitted (its principal axis) to the samples at least
    /// <paramref name="clear"/> from <paramref name="corner"/>; two points on it, or null with too few samples.</summary>
    private static (PointD P, PointD Q)? Leg(List<PointD> pts, int from, int to, PointD corner, double clear)
    {
        double step = 0.1 * clear;
        var samples = new List<PointD>();
        for (int i = Math.Min(from, to); i < Math.Max(from, to); i++)
        {
            double len = Dist(pts[i], pts[i + 1]);
            for (double t = 0; t < len; t += step)
            {
                var p = new PointD(pts[i].x + (pts[i + 1].x - pts[i].x) * t / len, pts[i].y + (pts[i + 1].y - pts[i].y) * t / len);
                if (Dist(p, corner) >= clear) samples.Add(p);
            }
        }
        if (samples.Count < 4) return null;
        double mx = samples.Average(p => p.x), my = samples.Average(p => p.y), sxx = 0, syy = 0, sxy = 0;
        foreach (var p in samples) { sxx += (p.x - mx) * (p.x - mx); syy += (p.y - my) * (p.y - my); sxy += (p.x - mx) * (p.y - my); }
        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (new PointD(mx, my), new PointD(mx + Math.Cos(angle), my + Math.Sin(angle)));
    }

    /// <summary>The cosine of the angle at <paramref name="b"/> between <paramref name="a"/> and <paramref name="c"/>.</summary>
    private static double Cos(PointD a, PointD b, PointD c) =>
        ((a.x - b.x) * (c.x - b.x) + (a.y - b.y) * (c.y - b.y)) / Math.Max(1e-12, Dist(a, b) * Dist(c, b));

    /// <summary>Where the line through <paramref name="a"/>, <paramref name="b"/> meets the line through
    /// <paramref name="c"/>, <paramref name="d"/>; null when they are near parallel.</summary>
    private static PointD? Meet(PointD a, PointD b, PointD c, PointD d)
    {
        double rx = b.x - a.x, ry = b.y - a.y, sx = d.x - c.x, sy = d.y - c.y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-9 * Math.Max(1, Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy)))) return null;
        double t = ((c.x - a.x) * sy - (c.y - a.y) * sx) / den;
        return new PointD(a.x + t * rx, a.y + t * ry);
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    private (IReadOnlyList<SilkTextLine> Lines, int Unread) Read()
    {
        if (_read is { } done) return done;
        _templates ??= GlyphTemplates.ForUser();
        var strokes = _strokes.Where(s => s.Length >= 4)
                              .Select(s => new SilkStroke(RestoreCorners([.. s.Select(v => (double)v)], _widthDbu), _widthDbu)).ToList();
        var (lines, unread) = StrokeGlyphs.Read(strokes, _templates, _bottom);
        _read = ([.. lines.Select(l => l with { Layer = Layer })], unread);
        return _read.Value;
    }
}
