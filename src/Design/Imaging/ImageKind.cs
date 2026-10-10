// What kind of drawing a picture is — brief-img-2-image-source-and-kind.md R-im2-2, R-im2-3 (D3, D16).
//
// Measured features, not a model: how much of the picture is ink, how much of the ink is FILLED rather than stroked,
// whether the strokes share one width, how much of the line work runs straight, how many colours the ink has, how much
// of the picture is flat (a drawing) rather than smoothly graded (a photograph), and how much of the ink sits in small
// pieces in rows (text). Each threshold is a named constant here with its measured reason in
// docs/design/image-to-circuit.md §5.2.
//
// The answer is a SUGGESTION. A caller may force Schematic or Layout (R-im2-3), and a forced read of a None picture is
// not refused for its kind — only later, if nothing at all is recognised (D16).

namespace CircuitRF.Design.Imaging;

/// <summary>What a picture holds: a schematic, a layout, or no drawing at all.</summary>
public enum DrawingKind { None, Schematic, Layout }

/// <summary>One measured feature and which way it reads.</summary>
/// <param name="Name">What was measured, in words (<c>filled share</c>, <c>one-width share</c>, …).</param>
/// <param name="Value">The measurement — a share is 0…1, a count is a count, a width is pixels.</param>
/// <param name="Reads">Which way it points: <c>layout</c>, <c>schematic</c>, <c>neither</c>, or the None reason it
/// supports.</param>
public sealed record ImageKindEvidence(string Name, double Value, string Reads);

/// <summary>The kind of a picture, how sure, and why.</summary>
/// <param name="Confidence">0.5 (a coin toss) … 1 (no doubt). For <see cref="DrawingKind.None"/>, how sure the picture
/// holds no drawing.</param>
/// <param name="Reason">For <see cref="DrawingKind.None"/>, the short phrase saying what was seen; null otherwise.</param>
/// <param name="Forced">The kind was chosen by the user, not read (R-im2-3); <see cref="Suggested"/> keeps the reading.</param>
public sealed record ImageKindResult(
    DrawingKind Kind, double Confidence, string? Reason, IReadOnlyList<ImageKindEvidence> Evidence,
    bool Forced = false, DrawingKind? Suggested = null)
{
    /// <summary>This reading with the kind forced to <paramref name="kind"/> (Schematic or Layout): the evidence and
    /// the reading it overrode are kept, so the provenance can say both.</summary>
    public ImageKindResult Force(DrawingKind kind)
    {
        if (kind == DrawingKind.None) throw new ArgumentException("Only Schematic or Layout can be forced.", nameof(kind));
        return this with { Kind = kind, Forced = true, Suggested = Suggested ?? Kind, Reason = null };
    }

    /// <summary>The wire spelling: <c>schematic</c>, <c>layout</c> or <c>none</c>.</summary>
    public string Name => ImageKind.Name(Kind);
}

public static class ImageKind
{
    // ── The None rules (D16) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A picture narrower or shorter than this is refused: below it a stroke and a letter are a few pixels.</summary>
    public const int MinSide = 64;

    /// <summary>Ink below this share of the picture is a blank picture: a 600 × 400 picture at this share holds 120
    /// ink pixels, one short line — scanner dust and a stray mark, not a drawing.</summary>
    public const double BlankInkShare = 0.0005;

    /// <summary>A pixel is FLAT when every channel is within this many sRGB levels of its right and lower neighbours.
    /// A rendered drawing is flat everywhere but its anti-aliased edges; JPEG's ringing stays within it at the qualities
    /// a figure is saved at.</summary>
    public const int FlatLevels = 3;

    /// <summary>Below this flat share a picture is smoothly graded — a photograph. Every rendered drawing measured is
    /// above 0.85 (the edges of a dense layout are the most that is not flat); a photograph or a noisy gradient is
    /// below 0.2.</summary>
    public const double PhotographFlatShare = 0.6;

    /// <summary>Below this flat share a picture is a photograph WHATEVER its line work reads: nothing in it is flat, so
    /// the ink mask is the noise itself, and the skeleton of noise is a mesh whose merged junctions join into long,
    /// perfectly straight edges — a Gaussian-noise gradient measured a straight-run share of 0.34.</summary>
    public const double PhotographFlatShareFloor = 0.2;

    /// <summary>Line work: the share of skeleton length in straight runs. Below this, the picture has none worth the
    /// name — a photograph's skeleton is noise, a page of text is curves too short to count.</summary>
    public const double LineWorkStraightShare = 0.3;

    /// <summary>A straight run counts when it is longer than this many stroke widths — a wire, not a letter's stem.</summary>
    public const double StraightRunStrokes = 10.0;

    /// <summary>Text above this share of the ink, with no line work, is a page of text.</summary>
    public const double TextPageShare = 0.6;

    // ── Schematic against layout ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ink share: a schematic is 2–8 % ink, a layout 15–60 %. The vote is linear across ±
    /// <see cref="InkShareSpan"/> about this centre.</summary>
    public const double InkShareCentre = 0.11;
    public const double InkShareSpan = 0.05;

    /// <summary>An ink pixel is FILLED when it lies deeper than this share of the picture's shorter side from the
    /// background (and at least <see cref="FilledMinDepthPx"/>). A schematic's strokes are 1/150–1/400 of its side, so
    /// none of their pixels is that deep; a layout's pads and pours are mostly deeper.</summary>
    public const double FilledDepthShare = 0.006;
    public const double FilledMinDepthPx = 3.0;

    /// <summary>Filled share of the ink: a schematic is under 0.05 (its junction dots), a layout over 0.5.</summary>
    public const double FilledShareCentre = 0.3;
    public const double FilledShareSpan = 0.2;

    /// <summary>A skeleton edge is at the dominant width when its mean width is within this fraction of it — one pen
    /// draws a schematic.</summary>
    public const double OneWidthTolerance = 0.30;

    /// <summary>One-width share of the skeleton length: a schematic is above 0.8, a layout of mixed pads and traces
    /// below 0.5.</summary>
    public const double OneWidthCentre = 0.6;
    public const double OneWidthSpan = 0.2;

    /// <summary>A colour is chromatic above this CIELAB chroma. Two or more chromatic ink colours lean layout (copper
    /// and a second layer); it is a weak vote, because a schematic tool may draw wires and symbols in two colours.</summary>
    public const double ChromaticChroma = 20.0;

    /// <summary>The votes' weights: filled share is the strongest single tell, colour the weakest.</summary>
    public const double InkWeight = 1.0, FilledWeight = 1.5, OneWidthWeight = 1.0, ColourWeight = 0.5;

    // ── Text (the IM-9 grouping's first pass) ────────────────────────────────────────────────────────────────────

    /// <summary>A text-sized component is at least this tall — below it no glyph is readable.</summary>
    public const int TextMinHeightPx = 5;

    /// <summary>…and at most this share of the picture's height (or 14 px, whichever is more).</summary>
    public const double TextMaxHeightShare = 0.05;

    public static string Name(DrawingKind kind) => kind switch
    {
        DrawingKind.Schematic => "schematic",
        DrawingKind.Layout    => "layout",
        _                     => "none",
    };

    /// <summary>Parses <c>schematic</c>, <c>layout</c> or <c>auto</c> (null) — the spelling <c>--image-kind</c> takes.</summary>
    public static bool TryParseOverride(string text, out DrawingKind? kind)
    {
        kind = null;
        switch (text.Trim().ToLowerInvariant())
        {
            case "auto":      return true;
            case "schematic": kind = DrawingKind.Schematic; return true;
            case "layout":    kind = DrawingKind.Layout; return true;
            default:          return false;
        }
    }

    /// <summary>Reads what kind of drawing <paramref name="raster"/> is.</summary>
    public static ImageKindResult Classify(RasterImage raster)
    {
        int w = raster.Width, h = raster.Height;
        var evidence = new List<ImageKindEvidence>();
        if (w < MinSide || h < MinSide)
            return None($"too small to read ({w} × {h} px)", 1.0, evidence);

        // Flat share first: it needs nothing but the pixels.
        double flat = FlatShare(raster);
        bool photographic = flat < PhotographFlatShare;
        evidence.Add(new("flat share", Round(flat), photographic ? "photograph" : "drawing"));

        var clusters = ColourClusters.Find(raster);
        int background = clusters.BackgroundIndex();
        evidence.Add(new("colours", clusters.Clusters.Count, clusters.Clusters.Count == 1 ? "blank" : "neither"));

        var ink = new BinaryImage(w, h);
        long inkCount = 0;
        for (int y = 0, i = 0; y < h; y++)
            for (int x = 0; x < w; x++, i++)
                if (clusters.Label(x, y) != background) { ink.Pixels[i] = 1; inkCount++; }
        double inkShare = (double)inkCount / ((long)w * h);
        if (clusters.Clusters.Count == 1 || inkShare < BlankInkShare)
        {
            evidence.Add(new("ink share", Round(inkShare), "blank"));
            return None("a blank picture", 1.0, evidence);
        }

        // Filled share: ink deeper than a stroke of a drawing this size could be.
        var dt = DistanceTransform.Compute(ink);
        double depth = Math.Max(FilledMinDepthPx, FilledDepthShare * Math.Min(w, h));
        long filled = 0;
        for (int i = 0; i < dt.Length; i++) if (dt[i] > depth) filled++;
        double filledShare = (double)filled / inkCount;

        // The skeleton: one-width share and straight-run share.
        var graph = SkeletonGraph.Build(ink);
        double sw = Math.Max(1.0, graph.StrokeWidth);
        double skeletonLength = 0, oneWidth = 0;
        foreach (var e in graph.Edges)
        {
            skeletonLength += e.Pixels.Count;
            if (Math.Abs(e.MeanWidth - sw) <= OneWidthTolerance * sw) oneWidth += e.Pixels.Count;
        }
        double oneWidthShare = skeletonLength > 0 ? oneWidth / skeletonLength : 0;
        double straight = 0;
        foreach (var s in Segments.FromSkeleton(graph))
            if (s.Length > StraightRunStrokes * sw) straight += s.Length;
        double straightShare = skeletonLength > 0 ? Math.Min(1.0, straight / skeletonLength) : 0;
        bool lineWork = straightShare >= LineWorkStraightShare;
        evidence.Add(new("stroke width", Round(sw), "neither"));
        evidence.Add(new("straight-run share", Round(straightShare), lineWork ? "line work" : "no line work"));

        double textShare = TextShare(ink, inkCount);
        evidence.Add(new("text share", Round(textShare), textShare > TextPageShare ? "text" : "neither"));

        if (flat < PhotographFlatShareFloor || (photographic && !lineWork))
            return None("a photograph-like picture with no straight line work",
                0.5 + 0.5 * Math.Clamp((PhotographFlatShare - flat) / PhotographFlatShare, 0, 1), evidence);
        if (textShare > TextPageShare && !lineWork)
            return None("a page of text", 0.5 + 0.5 * Math.Clamp((textShare - TextPageShare) / (1 - TextPageShare), 0, 1), evidence);

        int chromatic = 0;
        foreach (var c in clusters.Clusters)
            if (c.Index != background && Math.Sqrt(c.Lab.A * c.Lab.A + c.Lab.B * c.Lab.B) > ChromaticChroma) chromatic++;

        double vInk = Vote(inkShare, InkShareCentre, InkShareSpan);
        double vFilled = Vote(filledShare, FilledShareCentre, FilledShareSpan);
        double vWidth = -Vote(oneWidthShare, OneWidthCentre, OneWidthSpan);
        double vColour = chromatic >= 2 ? 1.0 : 0.0;
        evidence.Add(new("ink share", Round(inkShare), Leaning(vInk)));
        evidence.Add(new("filled share", Round(filledShare), Leaning(vFilled)));
        evidence.Add(new("one-width share", Round(oneWidthShare), Leaning(vWidth)));
        evidence.Add(new("chromatic ink colours", chromatic, Leaning(vColour)));

        double score = (InkWeight * vInk + FilledWeight * vFilled + OneWidthWeight * vWidth + ColourWeight * vColour)
                     / (InkWeight + FilledWeight + OneWidthWeight + ColourWeight);
        var kind = score >= 0 ? DrawingKind.Layout : DrawingKind.Schematic;
        return new ImageKindResult(kind, Round(0.5 + 0.5 * Math.Abs(score)), null, evidence);
    }

    private static ImageKindResult None(string reason, double confidence, List<ImageKindEvidence> evidence) =>
        new(DrawingKind.None, Round(confidence), reason, evidence);

    /// <summary>−1 (schematic) … +1 (layout), linear across ± span about the centre.</summary>
    private static double Vote(double value, double centre, double span) => Math.Clamp((value - centre) / span, -1, 1);

    private static string Leaning(double vote) => vote > 0.1 ? "layout" : vote < -0.1 ? "schematic" : "neither";

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>The share of pixels within <see cref="FlatLevels"/> of their right and lower neighbours on every
    /// channel (the last row and column compared one way).</summary>
    internal static double FlatShare(RasterImage img)
    {
        int w = img.Width, h = img.Height;
        var p = img.Rgba;
        long flat = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool ok = true;
                if (x + 1 < w) ok = Near(p, i, i + 4);
                if (ok && y + 1 < h) ok = Near(p, i, i + w * 4);
                if (ok) flat++;
            }
        return (double)flat / ((long)w * h);

        static bool Near(byte[] p, int a, int b) =>
            Math.Abs(p[a] - p[b]) <= FlatLevels && Math.Abs(p[a + 1] - p[b + 1]) <= FlatLevels && Math.Abs(p[a + 2] - p[b + 2]) <= FlatLevels;
    }

    /// <summary>The share of the ink in text-sized components that sit beside another of a like height on one row —
    /// the first pass of IM-9's grouping, enough to tell a page of text from a drawing.</summary>
    internal static double TextShare(BinaryImage ink, long inkCount)
    {
        var labels = Components.Label(ink);
        int maxH = Math.Max(14, (int)(TextMaxHeightShare * ink.Height));
        var small = new List<Component>();
        foreach (var c in labels.Components)
        {
            int ch = c.Bottom - c.Top + 1, cw = c.Right - c.Left + 1;
            if (ch >= TextMinHeightPx && ch <= maxH && cw <= 3 * ch) small.Add(c);
        }
        small.Sort((a, b) => a.CentroidY != b.CentroidY ? a.CentroidY.CompareTo(b.CentroidY) : a.Label.CompareTo(b.Label));

        var inRow = new bool[small.Count];
        for (int i = 0; i < small.Count; i++)
        {
            var a = small[i];
            int ha = a.Bottom - a.Top + 1;
            for (int j = i + 1; j < small.Count; j++)
            {
                var b = small[j];
                int hb = b.Bottom - b.Top + 1;
                if (b.CentroidY - a.CentroidY > maxH) break;
                double hm = Math.Max(ha, hb);
                if (Math.Abs(b.CentroidY - a.CentroidY) > 0.35 * hm) continue;
                if (Math.Max(ha, hb) > 1.6 * Math.Min(ha, hb)) continue;
                int gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right) - 1;
                if (gap > 1.2 * hm) continue;
                inRow[i] = inRow[j] = true;
            }
        }
        long area = 0;
        for (int i = 0; i < small.Count; i++) if (inRow[i]) area += small[i].Area;
        return inkCount > 0 ? (double)area / inkCount : 0;
    }
}
