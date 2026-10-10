// A schematic picture split into what it is made of — wires, words and symbols — with which wire ends reach which
// symbol. brief-img-7-schematic-wires-and-regions.md R-im7-1 … R-im7-8 (overview D9, D11, D16).
//
//   picture ─ normalise: darkest colours → binarise → straighten (± 5°) → stroke width w → border, dot grid out
//           ─ words (TextRegions): small pieces off the long runs, in lines; their pixels removed
//           ─ wires (WireGraph): the skeleton's straight runs, symbol strokes set aside, nodes, dots, crossings
//           ─ symbols (SymbolRegions): the residue, grown and merged; attachments; hops, supply marks, net labels
//
// Nothing here names a component (IM-8) or reads a word (IM-9). Pure: it posts nothing and writes nothing. Every
// tolerance is in units of w, the one number measured from the picture. The coordinates of everything returned are
// the READ picture's — the source turned by the deskew, when there was one; ToSource takes a point back.

using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Engine;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A box of pixels, inclusive, y down.</summary>
public readonly record struct PixelBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
    public double CentreX => (Left + Right + 1) / 2.0;
    public double CentreY => (Top + Bottom + 1) / 2.0;

    public PixelBox Union(PixelBox o) =>
        new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    /// <summary>The distance from (x, y) to the box's area (pixel edges); 0 inside.</summary>
    public double Distance(double x, double y)
    {
        double dx = Math.Max(0, Math.Max(Left - x, x - (Right + 1)));
        double dy = Math.Max(0, Math.Max(Top - y, y - (Bottom + 1)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The gap between two boxes; 0 when they touch or overlap.</summary>
    public double Distance(PixelBox o)
    {
        double dx = Math.Max(0, Math.Max(Left - (o.Right + 1), o.Left - (Right + 1)));
        double dy = Math.Max(0, Math.Max(Top - (o.Bottom + 1), o.Top - (Bottom + 1)));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Everything read from a schematic picture (R-im7-1). Kept whole on a refusal, so the overlay can still show
/// what was seen.</summary>
public sealed record SchematicImageRead
{
    /// <summary>The stroke width w, pixels.</summary>
    public double StrokeWidth { get; init; }

    /// <summary>The turn applied to straighten the picture, degrees (0: none).</summary>
    public double DeskewDeg { get; init; }

    /// <summary>The source picture's size.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>The read picture's size — the source's, or larger when it was turned.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The border frame removed, if one was.</summary>
    public PixelBox? Border { get; init; }

    public int GridDotsRemoved { get; init; }

    public IReadOnlyList<TextRegion> Words { get; init; } = [];

    /// <summary>Word-shaped pieces kept as symbol details because they touch line work.</summary>
    public IReadOnlyList<KeptText> KeptText { get; init; } = [];

    public WireGraph Wires { get; init; } = WireGraph.Empty;

    public IReadOnlyList<SymbolRegion> Symbols { get; init; } = [];

    /// <summary>Regions no wire reaches — removed, not passed on.</summary>
    public IReadOnlyList<PixelBox> Decorations { get; init; } = [];

    public IReadOnlyList<NetLabel> NetLabels { get; init; } = [];
    public IReadOnlyList<SupplyMark> SupplyMarks { get; init; } = [];

    /// <summary>End nodes reaching nothing: no symbol, no label, no supply mark.</summary>
    public IReadOnlyList<int> Dangling { get; init; } = [];

    public required SchematicImageReport Report { get; init; }

    /// <summary>Why nothing can be made of the picture, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    /// <summary>A point of the read picture in the source picture's pixels.</summary>
    public (double X, double Y) ToSource(double x, double y)
    {
        if (DeskewDeg == 0) return (x, y);
        double phi = DeskewDeg * Math.PI / 180, c = Math.Cos(phi), s = Math.Sin(phi);
        double rx = x - Width / 2.0, ry = y - Height / 2.0;
        return (rx * c + ry * s + SourceWidth / 2.0, -rx * s + ry * c + SourceHeight / 2.0);
    }
}

public static class SchematicImageReading
{
    /// <summary>Reads <paramref name="source"/>'s picture as a schematic (R-im7-1). Pure.</summary>
    public static SchematicImageRead Read(ImageSource source, SchematicImageOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Read(source.Raster, options, control);
    }

    internal static SchematicImageRead Read(RasterImage source, SchematicImageOptions? options = null, RunControl? control = null)
    {
        var o = options ?? new SchematicImageOptions();
        var token = control?.Token ?? CancellationToken.None;

        // ── Normalise (R-im7-2) ──
        control?.BeginStage("Reading the line work");
        var raster = source;
        var mask = Binarise(raster, o);
        double deskew = 0;
        if (o.Deskew)
        {
            double lean = Lean(mask, o);
            if (Math.Abs(lean) > o.MinDeskewDeg && Math.Abs(lean) <= o.MaxDeskewDeg)
            {
                deskew = -lean;
                raster = Rotate(source, deskew * Math.PI / 180);
                mask = Binarise(raster, o);
            }
        }
        token.ThrowIfCancellationRequested();
        double w = RunWidth(mask);
        PixelBox? border = o.RemoveBorder ? RemoveBorder(mask, w) : null;
        int grid = o.RemoveGrid ? RemoveGrid(mask, w) : 0;
        if (border is not null || grid > 0) w = RunWidth(mask);
        w = Math.Max(1.0, w);

        var read = new SchematicImageRead
        {
            StrokeWidth = w, DeskewDeg = deskew, SourceWidth = source.Width, SourceHeight = source.Height,
            Width = raster.Width, Height = raster.Height, Border = border, GridDotsRemoved = grid,
            Report = new SchematicImageReport(),
        };

        // ── Words (R-im7-3) ──
        control?.BeginStage("Finding words");
        var text = TextRegions.Find(mask, w, o);
        var wireMask = mask.Clone();
        for (int i = 0; i < wireMask.Pixels.Length; i++) if (text.Removed.Pixels[i] != 0) wireMask.Pixels[i] = 0;
        read = read with { Words = text.Words, KeptText = text.Kept };
        token.ThrowIfCancellationRequested();

        // ── Wires (R-im7-4, R-im7-5) ──
        control?.BeginStage("Tracing wires");
        var fills = Fills(wireMask, w, o);
        for (int i = 0; i < wireMask.Pixels.Length; i++) if (fills.Pixels[i] != 0) wireMask.Pixels[i] = 0;
        var dt = DistanceTransform.Compute(wireMask, o.MaxThreads);
        var blobs = WireGraph.Blobs(wireMask, dt, w, o);
        var skel = SkeletonGraph.Build(wireMask, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = o.MaxThreads });
        var pieces = WireGraph.Pieces(skel, w, o);
        var candidates = pieces.Where(p => p.IsWire).ToList();
        var strokes = WireGraph.SymbolStrokes(candidates, blobs, w, o);
        var wires = candidates.Where(p => !strokes.Contains(p.Sources[0])).ToList();
        token.ThrowIfCancellationRequested();
        if (wires.Count == 0)
            return read with { Report = Report(read), Refusal = SchematicImageReport.NoWiresRefusal };
        var first = WireGraph.Build(wires, [], blobs, w, o);

        // ── Symbols (R-im7-6, R-im7-7) ──
        control?.BeginStage("Finding symbols");
        var residue = SymbolRegions.Residue(wireMask, first, blobs, w);
        for (int i = 0; i < residue.Pixels.Length; i++) if (fills.Pixels[i] != 0) residue.Pixels[i] = 1;
        var raw = SymbolRegions.Group(residue, first, w, o);
        var (hops, hopRegions) = SymbolRegions.Hops(raw, mask.Width, first, w, o);
        var graph = hops.Count > 0 ? WireGraph.Build(wires, hops, blobs, w, o) : first;
        var regions = raw.Where((_, k) => !hopRegions.Contains(k)).ToList();
        var attached = SymbolRegions.Attach(regions, mask.Width, graph, text.Words, w, o);
        token.ThrowIfCancellationRequested();

        read = read with
        {
            Wires = graph, Symbols = attached.Regions, Decorations = attached.Decorations, NetLabels = attached.Labels,
            SupplyMarks = attached.Supplies, Dangling = attached.Dangling,
        };
        read = read with { Report = Report(read) };
        return attached.Regions.Count == 0 ? read with { Refusal = SchematicImageReport.NoSymbolsRefusal } : read;
    }

    private static SchematicImageReport Report(SchematicImageRead r) => new()
    {
        StrokeWidth = r.StrokeWidth,
        DeskewDeg = r.DeskewDeg,
        GridDotsRemoved = r.GridDotsRemoved,
        BorderRemoved = r.Border is not null,
        Words = r.Words.Count,
        WordsKeptAsSymbolDetail = r.KeptText.Count,
        WireSegments = r.Wires.Segments.Count,
        DiagonalWires = r.Wires.Segments.Count(s => s.Orientation == WireOrientation.Diagonal),
        JunctionDots = r.Wires.Nodes.Count(n => n.Dot),
        CrossingsConnected = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.Connected),
        CrossingsNotConnected = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.NotConnected),
        Hops = r.Wires.Crossings.Count(c => c.Reading == CrossingReading.Hop),
        SymbolRegions = r.Symbols.Count,
        DecorationsRemoved = r.Decorations.Count,
        NetLabels = r.NetLabels.Count,
        SupplyMarks = r.SupplyMarks.Count,
        DanglingEnds = r.Dangling.Count,
    };

    /// <summary>The filled areas of the drawing that are not junction dots — a filled rectangle, a diode's triangle,
    /// a supply's arrowhead: what survives an opening by a disc 1.5 w in radius, larger than a dot or far from round,
    /// with its anti-aliased rim. They are symbol, never wire: the skeleton runs straight through a filled rectangle
    /// between two collinear leads, and without this the whole symbol read as one wire with a dot on it.</summary>
    internal static BinaryImage Fills(BinaryImage mask, double w, SchematicImageOptions o)
    {
        var open = Morphology.Open(mask, 1.5 * w, o.MaxThreads);
        var lab = Components.Label(open);
        double dot = 1.15 * o.MaxDot * w;
        var keep = new HashSet<int>();
        foreach (var c in lab.Components)
        {
            int cw = c.Right - c.Left + 1, ch = c.Bottom - c.Top + 1;
            bool dotLike = cw <= dot && ch <= dot && Math.Max(cw, ch) <= 1.5 * Math.Min(cw, ch);
            if (!dotLike) keep.Add(c.Label);
        }
        var core = new BinaryImage(mask.Width, mask.Height);
        if (keep.Count == 0) return core;
        for (int i = 0; i < core.Pixels.Length; i++) if (keep.Contains(lab.Labels[i])) core.Pixels[i] = 1;
        var near = Morphology.Dilate(core, 1.0, o.MaxThreads);
        for (int i = 0; i < near.Pixels.Length; i++) near.Pixels[i] &= mask.Pixels[i];
        return near;
    }

    // ── Normalise ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strokes as foreground. A coloured drawing is first reduced to its darkest colours — those at
    /// least half as far from the ground as the darkest is — so a pale highlight or a tinted grid drops out while a
    /// coloured wire layer stays (on a dark ground, the lightest).</summary>
    internal static BinaryImage Binarise(RasterImage img, SchematicImageOptions o)
    {
        if (!HasColour(img)) return LineArt.Binarise(img, o.LineArt);
        var set = ColourClusters.Find(img, 8, new ColourClusterOptions { MaxThreads = o.MaxThreads });
        int bg = set.BackgroundIndex();
        double bgL = set.Clusters[bg].Lab.L;
        bool darkGround = bgL < 50;
        double far = darkGround ? set.Clusters.Max(c => c.Lab.L) : set.Clusters.Min(c => c.Lab.L);
        double cut = bgL + 0.5 * (far - bgL);
        var keep = set.Clusters.Where(c => c.Index != bg && (darkGround ? c.Lab.L >= cut : c.Lab.L <= cut)).Select(c => c.Index).ToList();
        if (keep.Count == 0) return LineArt.Binarise(img, o.LineArt);

        var cover = new float[img.Width * img.Height];
        foreach (int k in keep)
        {
            var v = set.Coverage(k).Values;
            for (int i = 0; i < cover.Length; i++) cover[i] += v[i];
        }
        var rgba = new byte[cover.Length * 4];
        for (int i = 0; i < cover.Length; i++)
        {
            byte g = (byte)Math.Clamp((int)Math.Round(255 * (1 - Math.Min(1f, cover[i]))), 0, 255);
            rgba[4 * i] = rgba[4 * i + 1] = rgba[4 * i + 2] = g;
            rgba[4 * i + 3] = 255;
        }
        return LineArt.Binarise(new RasterImage(img.Width, img.Height, rgba), o.LineArt with { Invert = false });
    }

    /// <summary>More than one pixel in two thousand is clearly coloured.</summary>
    private static bool HasColour(RasterImage img)
    {
        var p = img.Rgba;
        long n = 0, limit = Math.Max(1, (long)img.Width * img.Height / 2000);
        for (int j = 0; j < p.Length; j += 4)
        {
            int mx = Math.Max(p[j], Math.Max(p[j + 1], p[j + 2])), mn = Math.Min(p[j], Math.Min(p[j + 1], p[j + 2]));
            if (mx - mn > 48 && ++n > limit) return true;
        }
        return false;
    }

    /// <summary>The dominant lean of the long straight runs off 0°/90°, degrees, y down (length-weighted median); 0
    /// when there are none. Runs are cut where the skeleton leaves a line by more than 1.5 px: a line leaning 1.2° is
    /// binarised as a staircase of level steps, and cut at every step each piece would read level.</summary>
    internal static double Lean(BinaryImage mask, SchematicImageOptions o)
    {
        double w = Math.Max(1.0, RunWidth(mask));
        var g = SkeletonGraph.Build(mask, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = o.MaxThreads });
        var leans = new List<(double Dev, double Len)>();
        foreach (var e in g.Edges)
        {
            var pts = e.Pixels.Select(q => new PointD(q.X + 0.5, q.Y + 0.5)).ToList();
            var keep = Polylines.SimplifyOpen(pts, 1.5);
            for (int k = 1; k < keep.Count; k++)
            {
                var run = pts.GetRange(keep[k - 1], keep[k] - keep[k - 1] + 1);
                var fit = Fit.Segment(run);
                if (fit.Length < 8 * w || fit.Residual > 1.0) continue;
                double dev = fit.Angle * 180 / Math.PI;
                dev -= 90 * Math.Round(dev / 90);
                if (Math.Abs(dev) <= o.MaxDeskewDeg + 1) leans.Add((dev, fit.Length));
            }
        }
        if (leans.Count == 0) return 0;
        leans.Sort((x, y) => x.Dev.CompareTo(y.Dev));
        double half = leans.Sum(l => l.Len) / 2, acc = 0;
        foreach (var (dev, len) in leans)
        {
            acc += len;
            if (acc >= half) return dev;
        }
        return leans[^1].Dev;
    }

    /// <summary><paramref name="src"/> turned by <paramref name="phi"/> radians about its centre (y down) onto a
    /// white canvas large enough to hold it, sampled bilinearly.</summary>
    internal static RasterImage Rotate(RasterImage src, double phi)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi);
        int w = (int)Math.Ceiling(Math.Abs(src.Width * c) + Math.Abs(src.Height * s));
        int h = (int)Math.Ceiling(Math.Abs(src.Width * s) + Math.Abs(src.Height * c));
        var p = src.Rgba;
        var r = new byte[w * h * 4];
        double scx = src.Width / 2.0, scy = src.Height / 2.0, dcx = w / 2.0, dcy = h / 2.0;
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                double rx = u + 0.5 - dcx, ry = v + 0.5 - dcy;
                double sx = rx * c + ry * s + scx - 0.5, sy = -rx * s + ry * c + scy - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                int o = (v * w + u) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double Sample(int x, int y) =>
                        (uint)x < (uint)src.Width && (uint)y < (uint)src.Height ? p[(y * src.Width + x) * 4 + ch] : 255;
                    double val = (1 - fy) * ((1 - fx) * Sample(x0, y0) + fx * Sample(x0 + 1, y0))
                               + fy * ((1 - fx) * Sample(x0, y0 + 1) + fx * Sample(x0 + 1, y0 + 1));
                    r[o + ch] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                }
                r[o + 3] = 255;
            }
        return new RasterImage(w, h, r);
    }

    /// <summary>The stroke width: the mode, over the skeleton's path pixels, of the shorter of the horizontal and the
    /// vertical ink run through each. IM-1's 2·DT − 1 reads only odd widths on an orthogonal stroke (a 2 px wire is a
    /// distance of 1 from both sides' centres and reads 1); a run reads 2.</summary>
    internal static double RunWidth(BinaryImage mask)
    {
        var skel = Skeleton.Thin(mask);
        SkeletonGraph.RemoveStaircaseCorners(skel);
        int w = mask.Width, h = mask.Height;
        var samples = new List<double>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!skel[x, y] || SkeletonGraph.Neighbours(skel, x, y) != 2) continue;
                int l = x, r = x, t = y, b = y;
                while (mask.At(l - 1, y)) l--;
                while (mask.At(r + 1, y)) r++;
                while (mask.At(x, t - 1)) t--;
                while (mask.At(x, b + 1)) b++;
                samples.Add(Math.Min(r - l + 1, b - t + 1));
            }
        return StrokeWidth.Mode(samples);
    }

    /// <summary>Removes a frame enclosing most of the drawing — the component reaching over 60 % of the picture each
    /// way and inking at least 90 % of each side of its box — with everything drawn on it (a title block, zone
    /// marks). Returns its box.</summary>
    internal static PixelBox? RemoveBorder(BinaryImage mask, double w)
    {
        int W = mask.Width, H = mask.Height;
        var lab = Components.Label(mask);
        int band = (int)Math.Ceiling(w) + 2;
        foreach (var c in lab.Components.OrderByDescending(c => c.Area))
        {
            if (c.Right - c.Left + 1 < 0.6 * W || c.Bottom - c.Top + 1 < 0.6 * H) continue;
            bool Side(bool horizontal, bool far)
            {
                int from = horizontal ? c.Left : c.Top, to = horizontal ? c.Right : c.Bottom, hit = 0;
                for (int a = from; a <= to; a++)
                {
                    bool any = false;
                    for (int k = 0; k < band && !any; k++)
                    {
                        int across = horizontal ? (far ? c.Bottom - k : c.Top + k) : (far ? c.Right - k : c.Left + k);
                        int x = horizontal ? a : across, y = horizontal ? across : a;
                        if (lab.Labels[y * W + x] == c.Label) any = true;
                    }
                    if (any) hit++;
                }
                return hit >= 0.9 * (to - from + 1);
            }
            if (!Side(true, false) || !Side(true, true) || !Side(false, false) || !Side(false, true)) continue;
            for (int i = 0; i < mask.Pixels.Length; i++) if (lab.Labels[i] == c.Label) mask.Pixels[i] = 0;
            return new PixelBox(c.Left, c.Top, c.Right, c.Bottom);
        }
        return null;
    }

    /// <summary>Removes a dot grid: at least 25 dot-sized components, most of them on one lattice. Returns how many
    /// were removed.</summary>
    internal static int RemoveGrid(BinaryImage mask, double w)
    {
        var lab = Components.Label(mask);
        double side = 2 * w + 2;
        var dots = lab.Components.Where(c => c.Right - c.Left + 1 <= side && c.Bottom - c.Top + 1 <= side).ToList();
        if (dots.Count < 25) return 0;

        int Pitch(bool alongX)
        {
            var steps = new Dictionary<int, int>();
            foreach (var a in dots)
            {
                double best = double.MaxValue;
                foreach (var b in dots)
                {
                    double d = alongX ? b.CentroidX - a.CentroidX : b.CentroidY - a.CentroidY;
                    double off = alongX ? Math.Abs(b.CentroidY - a.CentroidY) : Math.Abs(b.CentroidX - a.CentroidX);
                    if (d >= 3 * w && off <= 1.5 && d < best) best = d;
                }
                if (best == double.MaxValue) continue;
                int k = (int)Math.Round(best);
                steps[k] = steps.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            return steps.Count == 0 ? 0 : steps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        }
        int px = Pitch(true), py = Pitch(false);
        if (px == 0 || py == 0) return 0;

        double Phase(Func<Component, double> at, int pitch) =>
            dots.GroupBy(c => (int)Math.Round(((at(c) % pitch) + pitch) % pitch))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        double phx = Phase(c => c.CentroidX, px), phy = Phase(c => c.CentroidY, py);
        bool On(double v, double phase, int pitch)
        {
            double m = ((v - phase) % pitch + pitch) % pitch;
            return Math.Min(m, pitch - m) <= 1.5;
        }
        var lattice = dots.Where(c => On(c.CentroidX, phx, px) && On(c.CentroidY, phy, py)).Select(c => c.Label).ToHashSet();
        if (lattice.Count < 25 || lattice.Count < 0.6 * dots.Count) return 0;
        for (int i = 0; i < mask.Pixels.Length; i++) if (lattice.Contains(lab.Labels[i])) mask.Pixels[i] = 0;
        return lattice.Count;
    }
}
