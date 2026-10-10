// Stroked silkscreen text read as glyphs — brief-artsch-10-silkscreen-ocr.md R-as10-1 … R-as10-3; overview D10 (4);
// docs/design/artwork-to-schematic.md §8.
//
// VECTOR GLYPH MATCHING, NOT OCR OF A PICTURE. A Gerber legend's text is drawn by a stroke font: each glyph is a few
// pen-down polylines, and the import keeps every one as a path with a centre line and a width. Those centre lines are
// the input. Nothing is rasterised and nothing is trained: a glyph is compared with a template by where its strokes
// run, in a frame where the line's cap height is 1.
//
//   strokes ─► units        strokes whose centre lines touch or cross (a glyph drawn as several strokes)
//           ─► text lines   units of one height, side by side on one band, at 0° or 90° (180°/270° and the bottom
//                           side's mirror image are read off the same line by trying the four frames of its family)
//           ─► glyphs       units split by gaps along the line, normalised to the line, matched
//
// The distance is a symmetric Chamfer one — each glyph's resampled centre line against the other's segments, both
// ways — so it does not care in which order or direction the strokes were drawn, nor how a CAD tool split them.

using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>One pen stroke of silkscreen: its centre line as flat x,y pairs, DBU, and the pen's width.</summary>
public sealed record SilkStroke(double[] Xy, double Width);

/// <summary>
/// A glyph normalised to its line: the line's cap height is 1, its baseline is y = 0, and the glyph's own box is
/// centred on x = 0 — so a <c>-</c> keeps its height above the baseline and an <c>_</c> its place below it.
/// </summary>
public sealed class Glyph
{
    /// <summary>Points resampled along the centre lines (R-as10-3's fixed count).</summary>
    public const int SampleCount = 48;

    /// <param name="sampleCount">How many points to resample the centre lines to: <see cref="SampleCount"/> for a glyph;
    /// a symbol (IM-8), with several times a glyph's ink, takes more.</param>
    public Glyph(IReadOnlyList<double[]> strokes, int sampleCount = SampleCount)
    {
        Strokes = strokes;
        var segs = new List<double>();
        foreach (var s in strokes)
            for (int i = 0; i + 3 < s.Length; i += 2) segs.AddRange([s[i], s[i + 1], s[i + 2], s[i + 3]]);
        if (segs.Count == 0)
            foreach (var s in strokes)
                if (s.Length >= 2) segs.AddRange([s[0], s[1], s[0], s[1]]);   // a dot is a segment of no length
        Segments = [.. segs];
        Samples = Resample(strokes, sampleCount);
        if (strokes.SelectMany(s => s).Any())
        {
            Width = strokes.SelectMany(s => Xs(s)).Max() - strokes.SelectMany(s => Xs(s)).Min();
        }
    }

    /// <summary>The centre lines, normalised.</summary>
    public IReadOnlyList<double[]> Strokes { get; }

    /// <summary>The glyph's width, in cap heights.</summary>
    public double Width { get; }

    internal double[] Samples { get; }
    internal double[] Segments { get; }

    private static IEnumerable<double> Xs(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return s[i]; }

    /// <summary>
    /// A symmetric Chamfer distance: the mean distance from each one's samples to the other's centre lines, the
    /// larger of the two directions (the modified Hausdorff form). In cap heights; 0 for the same strokes however they
    /// were split. The larger, not the average: a 3 lies wholly on an 8 of the same font, and an average halves the
    /// 8's whole missing side.
    /// </summary>
    public static double Distance(Glyph a, Glyph b) => Math.Max(OneWay(a.Samples, b.Segments), OneWay(b.Samples, a.Segments));

    private static double OneWay(double[] samples, double[] segs)
    {
        if (samples.Length == 0 || segs.Length == 0) return double.PositiveInfinity;
        double sum = 0;
        for (int i = 0; i < samples.Length; i += 2)
        {
            double px = samples[i], py = samples[i + 1], best = double.MaxValue;
            for (int k = 0; k < segs.Length; k += 4)
            {
                double d = SegmentDistance2(px, py, segs[k], segs[k + 1], segs[k + 2], segs[k + 3]);
                if (d < best) best = d;
            }
            sum += Math.Sqrt(best);
        }
        return sum / (samples.Length / 2);
    }

    internal static double SegmentDistance2(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double qx = ax + t * dx - px, qy = ay + t * dy - py;
        return qx * qx + qy * qy;
    }

    /// <summary><paramref name="count"/> points spread evenly by length over every stroke, flat x,y.</summary>
    private static double[] Resample(IReadOnlyList<double[]> strokes, int count)
    {
        double total = 0;
        foreach (var s in strokes)
            for (int i = 0; i + 3 < s.Length; i += 2) total += Math.Sqrt(Sq(s[i + 2] - s[i]) + Sq(s[i + 3] - s[i + 1]));
        if (total <= 0) return [.. strokes.Where(s => s.Length >= 2).SelectMany(s => new[] { s[0], s[1] })];

        var pts = new double[count * 2];
        double step = total / count, next = step / 2, run = 0;
        int n = 0;
        foreach (var s in strokes)
            for (int i = 0; i + 3 < s.Length && n < count; i += 2)
            {
                double len = Math.Sqrt(Sq(s[i + 2] - s[i]) + Sq(s[i + 3] - s[i + 1]));
                while (n < count && next <= run + len)
                {
                    double t = len <= 0 ? 0 : (next - run) / len;
                    pts[2 * n] = s[i] + t * (s[i + 2] - s[i]);
                    pts[2 * n + 1] = s[i + 1] + t * (s[i + 3] - s[i + 1]);
                    n++;
                    next += step;
                }
                run += len;
            }
        return n == count ? pts : pts[..(2 * n)];
    }

    private static double Sq(double v) => v * v;
}

/// <summary>A glyph's match against one template character.</summary>
public readonly record struct GlyphMatch(char Char, double Distance);

/// <summary>How a line of text lies on the board.</summary>
/// <param name="Rotation">0, 90, 180 or 270 — counter-clockwise, the reading direction from +x.</param>
/// <param name="Mirrored">Read mirrored, as text on the bottom side is when seen from the top.</param>
public readonly record struct TextOrientation(int Rotation, bool Mirrored)
{
    public override string ToString() => Mirrored ? $"{Rotation}° mirrored" : $"{Rotation}°";
}

/// <summary>One glyph of a line, read.</summary>
/// <param name="Glyph">The glyph, normalised.</param>
/// <param name="Ranked">Every template character, nearest first (each character once).</param>
public sealed record GlyphReading(Glyph Glyph, IReadOnlyList<GlyphMatch> Ranked)
{
    public GlyphMatch Best => Ranked[0];

    /// <summary>1 − best / second-best among <paramref name="cls"/>: 0 when two characters fit equally, 1 when only
    /// one fits at all. Characters drawn alike (<see cref="StrokeGlyphs.Alike"/>) are not each other's runner-up.</summary>
    public (GlyphMatch Best, double Margin) In(Func<char, bool> cls)
    {
        GlyphMatch? first = null, second = null;
        foreach (var m in Ranked)
        {
            if (!cls(m.Char)) continue;
            if (first is null) first = m;
            else if (!StrokeGlyphs.Alike(first.Value.Char, m.Char)) { second = m; break; }
        }
        if (first is not { } f) return (new GlyphMatch('?', double.PositiveInfinity), 0);
        if (second is not { } s || s.Distance <= 0) return (f, second is null ? 1 : 0);
        return (f, 1 - f.Distance / s.Distance);
    }

    /// <summary>The margin over the whole template set.</summary>
    public double Margin => In(_ => true).Margin;
}

/// <summary>One line of stroked text read off the silkscreen.</summary>
/// <param name="Text">Each glyph's best character.</param>
/// <param name="Refdes">The line read as a designator — <c>^[A-Z]{1,3}[0-9]{1,4}$</c> with every glyph clear of its
/// runner-up — or null.</param>
/// <param name="Glyphs">The glyphs in reading order.</param>
/// <param name="Box">Where the line is, DBU.</param>
/// <param name="Height">Its cap height, DBU.</param>
/// <param name="Orientation">How it lies.</param>
/// <param name="Strokes">Which of the strokes read it is made of.</param>
/// <param name="Layer">The silkscreen layer it is on.</param>
public sealed record SilkTextLine(
    string Text, string? Refdes, IReadOnlyList<GlyphReading> Glyphs, Bbox Box, double Height,
    TextOrientation Orientation, IReadOnlyList<int> Strokes, LayerKey? Layer = null)
{
    public long X => (Box.MinX + Box.MaxX) / 2;
    public long Y => (Box.MinY + Box.MaxY) / 2;

    /// <summary>When the line is no designator only because this many glyphs fit their runner-up about as well
    /// (a known prefix, every glyph near a character): how many. 0 otherwise.</summary>
    public int Uncertain { get; init; }
}

/// <summary>Reads text lines out of a set of strokes. R-as10-2, R-as10-3.</summary>
public static class StrokeGlyphs
{
    /// <summary>A glyph whose nearest template is farther than this, in cap heights, is not one of them.</summary>
    public const double MaxDistance = 0.1;

    /// <summary>A designator's glyphs must each beat their runner-up by this (1 − best/second).</summary>
    public const double MinMargin = 0.2;

    /// <summary>Two units are glyphs of one line when they are side by side with at most this gap, in cap heights.</summary>
    public const double MaxLetterGap = 0.6;

    /// <summary>Units this close along the line, in cap heights, are one glyph.</summary>
    public const double GlyphJoinGap = 0.04;

    private static readonly Regex RefdesShape = new("^[A-Z]{1,3}[0-9]{1,4}$", RegexOptions.CultureInvariant);

    /// <summary>The lines of text among <paramref name="strokes"/>, and how many strokes no line took.</summary>
    /// <param name="bottomSide">The strokes are on the bottom side, where text is mirrored: a mirrored reading
    /// is preferred there, and an unmirrored one on the top, when both fit equally.</param>
    public static (IReadOnlyList<SilkTextLine> Lines, int UnreadStrokes) Read(
        IReadOnlyList<SilkStroke> strokes, GlyphTemplates templates, bool bottomSide = false,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(templates);
        var units = Units(strokes);

        // Every candidate line of both families, read; then the best taken first, one line per unit.
        var candidates = new List<SilkTextLine>();
        var unitsOf = new List<int[]>();
        foreach (bool vertical in new[] { false, true })
            foreach (var group in Groups(units, vertical))
            {
                token.ThrowIfCancellationRequested();
                if (ReadLine(strokes, units, group, vertical, templates, bottomSide) is { } line)
                {
                    candidates.Add(line);
                    unitsOf.Add([.. group]);
                }
            }

        var order = Enumerable.Range(0, candidates.Count)
            .OrderByDescending(i => candidates[i].Refdes is not null)
            .ThenByDescending(i => candidates[i].Glyphs.Count)
            .ThenBy(i => candidates[i].Glyphs.Average(g => g.Best.Distance))
            .ToList();
        var used = new HashSet<int>();
        var lines = new List<SilkTextLine>();
        foreach (int i in order)
        {
            if (unitsOf[i].Any(used.Contains)) continue;
            lines.Add(candidates[i]);
            used.UnionWith(unitsOf[i]);
        }
        int unread = units.Where((_, i) => !used.Contains(i)).Sum(u => u.Strokes.Count);
        return (lines, unread);
    }

    // ── units: strokes whose centre lines touch or cross ────────────────────────────────────────────────

    internal sealed class Unit
    {
        public readonly List<int> Strokes = [];
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;

        public void Add(int index, double[] xy)
        {
            Strokes.Add(index);
            for (int i = 0; i + 1 < xy.Length; i += 2)
            {
                MinX = Math.Min(MinX, xy[i]); MaxX = Math.Max(MaxX, xy[i]);
                MinY = Math.Min(MinY, xy[i + 1]); MaxY = Math.Max(MaxY, xy[i + 1]);
            }
        }

        public (double AMin, double AMax, double PMin, double PMax) Frame(bool vertical) =>
            vertical ? (MinY, MaxY, MinX, MaxX) : (MinX, MaxX, MinY, MaxY);
    }

    internal static List<Unit> Units(IReadOnlyList<SilkStroke> strokes)
    {
        int n = strokes.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }

        var box = strokes.Select(Box).ToArray();
        var order = Enumerable.Range(0, n).Where(i => strokes[i].Xy.Length >= 2).OrderBy(i => box[i].MinX).ToArray();
        double maxTol = strokes.Count == 0 ? 0 : strokes.Max(Tolerance);
        for (int a = 0; a < order.Length; a++)
        {
            int i = order[a];
            for (int b = a + 1; b < order.Length; b++)
            {
                int j = order[b];
                if (box[j].MinX > box[i].MaxX + maxTol) break;
                double tol = Math.Max(Tolerance(strokes[i]), Tolerance(strokes[j]));
                if (box[j].MinY > box[i].MaxY + tol || box[i].MinY > box[j].MaxY + tol || box[j].MinX > box[i].MaxX + tol) continue;
                if (Find(i) != Find(j) && Touch(strokes[i].Xy, strokes[j].Xy, tol)) parent[Find(i)] = Find(j);
            }
        }

        var units = new Dictionary<int, Unit>();
        foreach (int i in order)
        {
            int r = Find(i);
            if (!units.TryGetValue(r, out var u)) units[r] = u = new Unit();
            u.Add(i, strokes[i].Xy);
        }
        return [.. units.Values];
    }

    /// <summary>How close two centre lines must come to be one glyph's — under a third of the pen, so two glyphs
    /// whose inked strokes touch are still two.</summary>
    private static double Tolerance(SilkStroke s) => Math.Max(0.3 * s.Width, 1);

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(SilkStroke s)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int i = 0; i + 1 < s.Xy.Length; i += 2)
        {
            x0 = Math.Min(x0, s.Xy[i]); x1 = Math.Max(x1, s.Xy[i]);
            y0 = Math.Min(y0, s.Xy[i + 1]); y1 = Math.Max(y1, s.Xy[i + 1]);
        }
        return (x0, y0, x1, y1);
    }

    private static bool Touch(double[] a, double[] b, double tol)
    {
        double tol2 = tol * tol;
        if (NearEnds(a, b, tol2) || NearEnds(b, a, tol2)) return true;
        for (int i = 0; i + 3 < a.Length; i += 2)
            for (int k = 0; k + 3 < b.Length; k += 2)
                if (Cross(a[i], a[i + 1], a[i + 2], a[i + 3], b[k], b[k + 1], b[k + 2], b[k + 3])) return true;
        return false;
    }

    /// <summary>An end of <paramref name="a"/> on <paramref name="b"/>'s centre line.</summary>
    private static bool NearEnds(double[] a, double[] b, double tol2)
    {
        foreach (int e in new[] { 0, a.Length - 2 })
        {
            double px = a[e], py = a[e + 1];
            if (b.Length == 2 && Sq(b[0] - px) + Sq(b[1] - py) <= tol2) return true;
            for (int k = 0; k + 3 < b.Length; k += 2)
                if (Glyph.SegmentDistance2(px, py, b[k], b[k + 1], b[k + 2], b[k + 3]) <= tol2) return true;
        }
        return false;
    }

    private static bool Cross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double d1 = Orient(cx, cy, dx, dy, ax, ay), d2 = Orient(cx, cy, dx, dy, bx, by);
        double d3 = Orient(ax, ay, bx, by, cx, cy), d4 = Orient(ax, ay, bx, by, dx, dy);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Orient(double ax, double ay, double bx, double by, double px, double py) =>
        (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private static double Sq(double v) => v * v;

    // ── text lines: units of one height side by side on one band ───────────────────────────────────────

    /// <summary>
    /// The candidate lines of one family — reading along x (0°/180°) or along y (90°/270°). A unit is TALL in a
    /// family when its extent across the line is at least two thirds of its extent along it (a glyph is no wider
    /// than it is tall, give or take an <c>M</c>); tall units of one height that overlap across the line and sit
    /// within <see cref="MaxLetterGap"/> along it are one line. A short unit — a <c>-</c>, a <c>_</c> — joins a
    /// line whose band it sits in. Each line is offered whole and split at its widest gap, so two designators
    /// printed side by side are read as two.
    /// </summary>
    internal static List<List<int>> Groups(List<Unit> units, bool vertical)
    {
        var f = units.Select(u => u.Frame(vertical)).ToArray();
        double H(int i) => f[i].PMax - f[i].PMin;
        double A(int i) => f[i].AMax - f[i].AMin;
        bool Tall(int i) => H(i) > 0 && A(i) <= 1.6 * H(i);

        var tall = Enumerable.Range(0, units.Count).Where(Tall).OrderBy(i => f[i].AMin).ToArray();
        var parent = new Dictionary<int, int>();
        foreach (int i in tall) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }

        for (int a = 0; a < tall.Length; a++)
        {
            int i = tall[a];
            double reach = f[i].AMax + MaxLetterGap * H(i) * 1.4;
            for (int b = a + 1; b < tall.Length && f[tall[b]].AMin <= reach; b++)
            {
                int j = tall[b];
                double hi = Math.Max(H(i), H(j)), lo = Math.Min(H(i), H(j));
                if (hi > 1.35 * lo) continue;
                double overlap = Math.Min(f[i].PMax, f[j].PMax) - Math.Max(f[i].PMin, f[j].PMin);
                if (overlap < 0.7 * lo) continue;
                double gap = f[j].AMin - f[i].AMax;
                if (gap < -0.2 * hi || gap > MaxLetterGap * hi) continue;
                parent[Find(i)] = Find(j);
            }
        }

        var groups = tall.GroupBy(Find).Select(g => g.ToList()).ToList();
        var shortUnits = Enumerable.Range(0, units.Count).Where(i => !Tall(i)).OrderBy(i => f[i].AMin).ToList();
        var lines = new List<List<int>>();
        foreach (var g in groups)
        {
            var heights = g.Select(H).OrderBy(h => h).ToList();
            double h = heights[heights.Count / 2];
            double pLo = Median(g.Select(i => f[i].PMin)), pHi = Median(g.Select(i => f[i].PMax));

            // Short units in the band, beside the line.
            var members = new List<int>(g);
            double aLo = g.Min(i => f[i].AMin), aHi = g.Max(i => f[i].AMax);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (int i in shortUnits)
                {
                    if (f[i].AMin > aHi + MaxLetterGap * h) break;
                    if (members.Contains(i) || H(i) >= 0.7 * h || A(i) > 1.3 * h) continue;
                    if (f[i].PMin < pLo - 0.3 * h || f[i].PMax > pHi + 0.3 * h) continue;
                    if (f[i].AMin > aHi + MaxLetterGap * h || f[i].AMax < aLo - MaxLetterGap * h) continue;
                    members.Add(i);
                    aLo = Math.Min(aLo, f[i].AMin); aHi = Math.Max(aHi, f[i].AMax);
                    grew = true;
                }
            }
            if (members.Count < 2) continue;
            members.Sort((x, y) => f[x].AMin.CompareTo(f[y].AMin));
            Split(members, f, h, lines);
        }
        return lines;
    }

    /// <summary>The line, and its two halves at the widest gap, recursively while a half has four units or more.</summary>
    private static void Split(List<int> members, (double AMin, double AMax, double PMin, double PMax)[] f, double h, List<List<int>> into)
    {
        into.Add(members);
        if (members.Count < 4) return;
        int cut = -1;
        double widest = 0.15 * h, reach = f[members[0]].AMax;
        for (int k = 1; k < members.Count; k++)
        {
            double gap = f[members[k]].AMin - reach;
            if (gap > widest) { widest = gap; cut = k; }
            reach = Math.Max(reach, f[members[k]].AMax);
        }
        if (cut < 0) return;
        var left = members[..cut];
        var right = members[cut..];
        if (left.Count >= 2) Split(left, f, h, into);
        if (right.Count >= 2) Split(right, f, h, into);
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    // ── one line, read in the four frames of its family ───────────────────────────────────────────────

    /// <summary>The four frames of a family: the map from board to reading coordinates (reading along +x, up +y),
    /// and the orientation it reads.</summary>
    private static IEnumerable<(Func<double, double, (double, double)> Map, TextOrientation Orientation)> Frames(bool vertical)
    {
        if (!vertical)
        {
            yield return ((x, y) => (x, y), new TextOrientation(0, false));
            yield return ((x, y) => (-x, -y), new TextOrientation(180, false));
            yield return ((x, y) => (-x, y), new TextOrientation(0, true));
            yield return ((x, y) => (x, -y), new TextOrientation(180, true));
        }
        else
        {
            yield return ((x, y) => (y, -x), new TextOrientation(90, false));
            yield return ((x, y) => (-y, x), new TextOrientation(270, false));
            yield return ((x, y) => (-y, -x), new TextOrientation(90, true));
            yield return ((x, y) => (y, x), new TextOrientation(270, true));
        }
    }

    private static SilkTextLine? ReadLine(IReadOnlyList<SilkStroke> strokes, List<Unit> units, List<int> members,
                                          bool vertical, GlyphTemplates templates, bool bottomSide)
    {
        var f = members.Select(i => units[i].Frame(vertical)).ToList();
        var tallHeights = f.Select(x => x.PMax - x.PMin).Where(h => h > 0).OrderBy(h => h).ToList();
        if (tallHeights.Count == 0) return null;
        double height = tallHeights[tallHeights.Count / 2];   // the upper median: short units never set it

        (List<GlyphReading> Glyphs, double Score, TextOrientation Orientation)? best = null;
        foreach (var (map, orientation) in Frames(vertical))
        {
            var glyphs = Glyphs(strokes, units, members, map, height);
            if (glyphs.Count < 2) continue;
            var read = glyphs.Select(g => new GlyphReading(g, templates.Match(g))).ToList();
            double score = read.Sum(r => r.Best.Distance)
                           + (orientation.Mirrored != bottomSide ? 0.005 * read.Count : 0);
            if (best is null || score < best.Value.Score) best = (read, score, orientation);
        }
        if (best is not { } chosen) return null;
        if (chosen.Glyphs.Average(g => g.Best.Distance) > MaxDistance) return null;   // not text: an outline, a logo
        var (refdes, uncertain) = Refdes(chosen.Glyphs);
        if (MostlyBars(chosen.Glyphs) && (refdes is null || !KnownPrefixes.Contains(Prefix(refdes)))) return null;

        var box = Bbox.Empty;
        foreach (int u in members)
            box = box.Union(new Bbox((long)Math.Floor(units[u].MinX), (long)Math.Floor(units[u].MinY),
                                     (long)Math.Ceiling(units[u].MaxX), (long)Math.Ceiling(units[u].MaxY)));
        return new SilkTextLine(
            new string([.. chosen.Glyphs.Select(g => g.Best.Char)]), refdes, chosen.Glyphs, box, height,
            chosen.Orientation, [.. members.SelectMany(u => units[u].Strokes).Order()]) { Uncertain = uncertain };
    }

    /// <summary>The members' strokes in one frame, split into glyphs along the line and normalised to it.</summary>
    private static List<Glyph> Glyphs(IReadOnlyList<SilkStroke> strokes, List<Unit> units, List<int> members,
                                      Func<double, double, (double, double)> map, double height)
    {
        var mapped = members.Select(u => units[u].Strokes.Select(s =>
        {
            var xy = strokes[s].Xy;
            var m = new double[xy.Length];
            for (int i = 0; i + 1 < xy.Length; i += 2) (m[i], m[i + 1]) = map(xy[i], xy[i + 1]);
            return m;
        }).ToList()).ToList();
        var boxes = mapped.Select(ss => (
            MinX: ss.SelectMany(Xs).Min(), MaxX: ss.SelectMany(Xs).Max(),
            MinY: ss.SelectMany(Ys).Min(), MaxY: ss.SelectMany(Ys).Max())).ToList();

        // Units overlapping along the line, or all but touching, are one glyph.
        var order = Enumerable.Range(0, mapped.Count).OrderBy(i => boxes[i].MinX).ToList();
        var clusters = new List<List<int>>();
        double reach = double.MinValue;
        foreach (int i in order)
        {
            if (clusters.Count == 0 || boxes[i].MinX > reach + GlyphJoinGap * height) clusters.Add([]);
            clusters[^1].Add(i);
            reach = Math.Max(clusters[^1].Count == 1 ? double.MinValue : reach, boxes[i].MaxX);
        }

        // The baseline: the median foot of the glyphs that are full height.
        var full = clusters.Where(c => c.Max(i => boxes[i].MaxY) - c.Min(i => boxes[i].MinY) >= 0.75 * height).ToList();
        if (full.Count == 0) return [];
        double baseline = Median(full.Select(c => c.Min(i => boxes[i].MinY)));

        var glyphs = new List<Glyph>(clusters.Count);
        foreach (var c in clusters)
        {
            double cx = (c.Min(i => boxes[i].MinX) + c.Max(i => boxes[i].MaxX)) / 2;
            var norm = c.SelectMany(i => mapped[i]).Select(s =>
            {
                var n = new double[s.Length];
                for (int k = 0; k + 1 < s.Length; k += 2) { n[k] = (s[k] - cx) / height; n[k + 1] = (s[k + 1] - baseline) / height; }
                return n;
            }).ToList();
            glyphs.Add(new Glyph(norm));
        }
        return glyphs;
    }

    private static IEnumerable<double> Xs(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return s[i]; }
    private static IEnumerable<double> Ys(double[] s) { for (int i = 1; i < s.Length; i += 2) yield return s[i]; }

    /// <summary>
    /// The prefixes designators are written with. A known prefix decides between readings of a line whose split into
    /// letters and digits is ambiguous, and only after one may a digit be read where a letter fits better — a
    /// <c>0</c> nearest <c>O</c>, an <c>8</c> nearest <c>B</c>. Single letters that are also common words' letters
    /// (<c>S</c>, <c>Z</c>, <c>E</c>) are left out on purpose: <c>ST</c> read as S7 and <c>22</c> as Z2 otherwise.
    /// </summary>
    public static IReadOnlySet<string> KnownPrefixes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "R", "C", "L", "FB", "FL", "J", "P", "X", "D", "Q", "U", "IC", "Y", "SW", "TP", "T", "K", "F", "VR", "RV",
        "RN", "CN", "JP", "ANT", "LED", "BT", "MH", "TR", "XTAL", "CR", "RT",
    };

    /// <summary>
    /// The glyphs read as a designator: one to three letters then one to four digits, the first digit not a zero.
    /// Each glyph is matched within its class — a letter position against letters, a digit position against digits —
    /// so <c>O</c>/<c>0</c> and <c>I</c>/<c>1</c> are told apart by where they stand. Of the splits, one whose letters
    /// are a <see cref="KnownPrefixes">known prefix</see> is taken first, then the one that fits best.
    ///
    /// <para>Null unless every glyph is within <see cref="MaxDistance"/> and clear of its runner-up in its class by
    /// <see cref="MinMargin"/>. A letter is never read where a digit fits better, and a digit only after a known
    /// prefix — otherwise ordinary words and numbers read as designators (<c>ST</c> as S7, <c>22</c> as Z2). A
    /// designator numbered from zero is rare, and that zero is where words like <c>TO</c> land, so a leading zero is
    /// refused. Two characters drawn alike (<see cref="Alike"/>) are not a coercion.</para>
    /// </summary>
    internal static (string? Refdes, int Uncertain) Refdes(IReadOnlyList<GlyphReading> glyphs)
    {
        int n = glyphs.Count;
        string? bestText = null;
        (bool Known, double Total) best = (false, double.MaxValue);
        int uncertain = 0;
        for (int letters = 1; letters <= Math.Min(3, n - 1); letters++)
        {
            if (n - letters > 4) continue;
            double total = 0;
            var chars = new char[n];
            bool ok = true, coerced = false;
            int unsure = 0;
            for (int i = 0; i < n && ok; i++)
            {
                var (m, margin) = glyphs[i].In(i < letters ? char.IsAsciiLetterUpper : char.IsAsciiDigit);
                ok = m.Distance <= MaxDistance;
                if (margin < MinMargin) unsure++;
                bool other = m.Char != glyphs[i].Best.Char && !Alike(m.Char, glyphs[i].Best.Char);
                if (other && i < letters) ok = false;
                coerced |= other;
                chars[i] = m.Char;
                total += m.Distance;
            }
            if (!ok || chars[letters] == '0') continue;
            bool known = KnownPrefixes.Contains(new string(chars, 0, letters));
            if (coerced && !known) continue;
            // A designator but for a glyph or two that fit their runner-up about as well: the line says how many.
            if (unsure > 0) { if (known) uncertain = uncertain == 0 ? unsure : Math.Min(uncertain, unsure); continue; }
            if ((known && !best.Known) || (known == best.Known && total < best.Total))
            {
                best = (known, total);
                bestText = new string(chars);
            }
        }
        return bestText is not null && RefdesShape.IsMatch(bestText) ? (bestText, 0) : (null, uncertain);
    }

    private static string Prefix(string refdes) => new([.. refdes.TakeWhile(char.IsAsciiLetter)]);

    /// <summary>Two characters the templates draw the same: <c>I</c> and a flagless <c>1</c>. Neither is the other's
    /// runner-up, and reading one as the other is not reading a glyph as a character it does not look like.</summary>
    public static bool Alike(char a, char b) => (a, b) is ('I', '1') or ('1', 'I');

    /// <summary>A line three quarters of whose glyphs are single bars — <c>I</c>, <c>1</c>, <c>-</c>, <c>_</c> — is a
    /// row of ticks, a hatch or a pin-1 bar, not text, unless it reads as a designator with a known prefix
    /// (<c>C11</c>).</summary>
    private static bool MostlyBars(IReadOnlyList<GlyphReading> glyphs) =>
        glyphs.Count(g => g.Best.Char is 'I' or '1' or '-' or '_') >= 0.75 * glyphs.Count;

    /// <summary>Glyphs of a line that would read as a designator under a known prefix but for fitting their
    /// runner-up about as well — the ones to correct in the parts table and learn.</summary>
    public static int LowMarginGlyphs(SilkTextLine line) => line.Uncertain;
}
