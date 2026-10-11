// A schematic picture's words, read — brief-img-9-text-and-values.md R-im9-1, R-im9-3, R-im9-5 (overview D1, D10).
//
// THE READER IS AS-10's. A word region's skeleton (TextRegions, R-im7-3) is a set of centre lines, which is exactly what
// StrokeGlyphs reads a silkscreen pen's strokes as: the same split into glyphs, the same normalisation to the line's cap
// height, the same modified-Hausdorff distance to the same Hershey templates — with the value glyphs added
// (GlyphTemplates.Text). Typeset sans-serif text skeletonises close to a single-stroke font, which is why this works;
// a serif or script face reads worse, and says so word by word through the distance — it is never refused.
//
// THE GRAMMAR DECIDES A TIE. Each glyph offers every character within AS-10's runner-up margin of its best; the
// combinations are read by ValueGrammar, and the best-ranked grammatical one wins (WordReading.Rank, then the total
// distance). The skeleton of a sans-serif l, I and a flagless 1 is one bar, so "l0pF" is three strings and only
// "10pF" is grammatical. A designator keeps AS-10's own rule on top: a letter is never read where a digit fits better,
// and a digit is read where a letter fits better only after a known prefix.
//
// A line of words. Glyphs farther apart than SpaceGap cap heights have a space between them. The line is read whole
// first — a line set is written with spaces — and, when that has no reading, word by word.

using Clipper2Lib;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>One word of a schematic picture, read (R-im9-1, R-im9-3).</summary>
/// <param name="Id">Its number among the picture's words, reading order.</param>
/// <param name="Region">The <see cref="TextRegion.Id"/> it came from (a region may hold several words).</param>
/// <param name="Box">Where it is, in the read picture's pixels.</param>
/// <param name="Reading">What it says, or null when it is unread.</param>
/// <param name="Seen">Each glyph's nearest character, as matched before the grammar — what an unread word looked like.</param>
/// <param name="Distance">The mean distance of the glyphs read to their templates, cap heights: how well it read.</param>
/// <param name="Glyphs">The glyphs, for teaching (R-im9-5); empty from a reader that has none.</param>
public sealed record ImageWord(int Id, int Region, PixelBox Box, WordReading? Reading, string Seen, double Distance,
                               IReadOnlyList<GlyphReading> Glyphs)
{
    public WordClass Class => Reading?.Class ?? WordClass.Unread;

    /// <summary>Unread because its type is too heavy for its size (<see cref="StrokeTextReader.MaxWeight"/>).</summary>
    public bool TooHeavy { get; init; }

    /// <summary>The reading's text, or what was seen when there is none.</summary>
    public string Text => Reading?.Text ?? Seen;
}

/// <summary>The words of a schematic picture.</summary>
public static class ImageText
{
    /// <summary>Reads every word region of <paramref name="read"/> with <paramref name="reader"/> (the stroke-glyph
    /// reader with the user's glyphs when null) and numbers the words in reading order. Pure.</summary>
    public static IReadOnlyList<ImageWord> Read(SchematicImageRead read, IImageTextReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        reader ??= new StrokeTextReader();
        var words = new List<ImageWord>();
        foreach (var region in read.Words) words.AddRange(reader.Read(region, read.StrokeWidth));
        words = JoinLineSets(words, read.Words);
        return [.. words.Select((w, i) => w with { Id = i })];
    }

    /// <summary>
    /// A line set written across a comma and a space — <c>Z=50Ω, E=90°</c> — is two word regions, the space being
    /// wider than a letter gap. Two line sets one after the other on one line, no further apart than their height, are
    /// one set when together they still are (each key once).
    /// </summary>
    private static List<ImageWord> JoinLineSets(List<ImageWord> words, IReadOnlyList<TextRegion> regions)
    {
        bool joined = true;
        while (joined)
        {
            joined = false;
            for (int i = 0; i < words.Count && !joined; i++)
                for (int j = 0; j < words.Count && !joined; j++)
                {
                    ImageWord a = words[i], b = words[j];
                    if (i == j || a.Class != WordClass.LineParameters || b.Class != WordClass.LineParameters) continue;
                    bool vertical = regions.FirstOrDefault(r => r.Id == a.Region)?.Vertical ?? false;
                    if (vertical != (regions.FirstOrDefault(r => r.Id == b.Region)?.Vertical ?? false)) continue;
                    double h = vertical ? Math.Max(a.Box.Width, b.Box.Width) : Math.Max(a.Box.Height, b.Box.Height);
                    var (aLo, aHi, bLo, bHi, aP0, aP1, bP0, bP1) = vertical
                        ? (a.Box.Top, a.Box.Bottom, b.Box.Top, b.Box.Bottom, a.Box.Left, a.Box.Right, b.Box.Left, b.Box.Right)
                        : (a.Box.Left, a.Box.Right, b.Box.Left, b.Box.Right, a.Box.Top, a.Box.Bottom, b.Box.Top, b.Box.Bottom);
                    if (bLo <= aLo || bLo - aHi > h || bLo - aHi < 0) continue;   // b follows a, across a space
                    if (Math.Min(aP1, bP1) - Math.Max(aP0, bP0) < 0.5 * Math.Min(aP1 - aP0, bP1 - bP0)) continue;
                    if (ValueGrammar.Parse(a.Reading!.Text + " " + b.Reading!.Text) is not { Class: WordClass.LineParameters } r) continue;
                    int n = a.Glyphs.Count + b.Glyphs.Count;
                    double d = n == 0 ? Math.Max(a.Distance, b.Distance) : (a.Distance * a.Glyphs.Count + b.Distance * b.Glyphs.Count) / n;
                    words[i] = new ImageWord(0, a.Region, a.Box.Union(b.Box), r, a.Seen + " " + b.Seen, d, [.. a.Glyphs, .. b.Glyphs]);
                    words.RemoveAt(j);
                    joined = true;
                }
        }
        return words;
    }

    /// <summary>
    /// What correcting a word teaches (R-im9-5): each glyph the corrected text says is another character, as a template
    /// of that character. A designator teaches designator glyphs, as AS-10's Learn These Glyphs does; anything else —
    /// a value, a line set, a name — teaches value glyphs, which the silkscreen reader never consults. Null when nothing
    /// differs, the word has no glyphs, or the correction has a different number of characters than the word has glyphs
    /// (then which glyph is which cannot be told).
    /// </summary>
    public static IReadOnlyList<(char Char, Glyph Glyph, GlyphClass Class)>? Lesson(ImageWord word, string corrected)
    {
        ArgumentNullException.ThrowIfNull(word);
        string said = new([.. (corrected ?? "").Where(c => !char.IsWhiteSpace(c))]);
        string read = new([.. word.Text.Where(c => !char.IsWhiteSpace(c))]);
        if (word.Glyphs.Count == 0 || said.Length != word.Glyphs.Count || read.Length != said.Length) return null;
        var cls = ValueGrammar.Parse(said)?.Class == WordClass.Designator ? GlyphClass.Designator : GlyphClass.Value;
        var lesson = new List<(char, Glyph, GlyphClass)>();
        for (int i = 0; i < said.Length; i++)
            if (said[i] != read[i] && !StrokeGlyphs.Alike(said[i], read[i])) lesson.Add((said[i], word.Glyphs[i].Glyph, cls));
        return lesson.Count == 0 ? null : lesson;
    }
}

/// <summary>The in-house reader: AS-10's glyph matcher under the grammar (this file's header).</summary>
public sealed class StrokeTextReader : IImageTextReader
{
    /// <summary>A word whose glyphs lie farther than this from their templates on average, in cap heights, is not
    /// text the templates know — a scribble, a logo — and is unread. Typeset sans-serif glyphs read at 0.02 … 0.07 (a W
    /// at 0.13); a random scrawl's pieces at 0.07 … 0.2, each near SOME letter, so the mean is what tells them apart.</summary>
    public const double MaxWordDistance = 0.09;

    /// <summary>A glyph farther than this from every template makes its word unread.</summary>
    public const double MaxGlyphDistance = 0.2;

    /// <summary>A gap wider than this between glyphs, in cap heights, is a space. A skeleton stops short of its ink and
    /// a typeset digit is narrower than its advance, so letters of one word stand up to ~0.6 apart; a space is ~0.9.</summary>
    public const double SpaceGap = 0.75;

    /// <summary>A glyph reading worse than this, in cap heights, and at least <see cref="MinCutWidth"/> wide may be two
    /// glyphs whose ink touches, and is tried cut in two.</summary>
    public const double CutAbove = 0.1;

    /// <summary>The narrowest glyph tried cut, in cap heights.</summary>
    public const double MinCutWidth = 0.7;

    /// <summary>
    /// A word whose strokes are wider than this share of its cap height is not read: its counters close up — an
    /// <c>=</c> becomes a bar, an <c>N</c>'s diagonal collapses — and it reads WRONG rather than not at all (IBM Plex
    /// Sans Bold at a 14 px cap: <c>IN</c> as the value <c>1H</c>, <c>DNP</c> as <c>QmP</c>). Regular faces stand at
    /// 0.12 … 0.20, bold ones at 0.23 … 0.29 at 14 and 24 px caps alike.
    /// </summary>
    public const double MaxWeight = 0.22;

    /// <summary>A glyph offers every character within this of its best, in cap heights, however well the best fits: a
    /// clean glyph at 0.03 would otherwise offer only what lies within 0.0375, and a typeset 5 and S, 0 and O, θ and 8
    /// stand 0.01 … 0.02 apart.</summary>
    public const double MinReach = 0.02;

    /// <summary>A glyph offers at most this many characters to the grammar.</summary>
    private const int MaxAlternatives = 4;

    /// <summary>The combinations kept while a word's readings are built, cheapest first.</summary>
    private const int Beam = 512;

    private GlyphTemplates? _templates;

    /// <param name="templates">The glyphs; null is the user's (built-in designator and value glyphs, plus taught).</param>
    public StrokeTextReader(GlyphTemplates? templates = null) => _templates = templates;

    public IReadOnlyList<ImageWord> Read(TextRegion region, double strokeWidth)
    {
        ArgumentNullException.ThrowIfNull(region);
        _templates ??= GlyphTemplates.ForUserText();
        double pen = Math.Max(1, strokeWidth);
        if (Weight(region) > MaxWeight)
            return [Unread(region.Box, region.Id, "", double.PositiveInfinity, []) with { TooHeavy = true }];

        // The skeleton in y-up coordinates, as a pen's strokes are, each kink at a free end trimmed (an end another
        // stroke meets is a junction, and trimming there would cut the glyph apart); a piece too small to have a
        // skeleton — a full stop — is a dot at its centre.
        var paths = region.Strokes.Where(p => p.Count >= 2).ToList();
        bool Free(int k, PointD end) => !paths.Where((_, j) => j != k).Any(q =>
            Math.Abs(q[0].x - end.x) + Math.Abs(q[0].y - end.y) <= 1.5 || Math.Abs(q[^1].x - end.x) + Math.Abs(q[^1].y - end.y) <= 1.5);
        var strokes = paths
            .Select((p, k) =>
            {
                var xy = new double[2 * p.Count];
                for (int i = 0; i < p.Count; i++) (xy[2 * i], xy[2 * i + 1]) = (p[i].x, -p[i].y);
                return new SilkStroke(TrimEndKinks(xy, pen, Free(k, p[0]), Free(k, p[^1])), pen);
            })
            .ToList();
        foreach (var g in region.Glyphs)
            if (!strokes.Any(st => Points(st.Xy).Any(q => q.X >= g.Left - 0.5 && q.X <= g.Right + 1.5 && -q.Y >= g.Top - 0.5 && -q.Y <= g.Bottom + 1.5)))
                strokes.Add(new SilkStroke([g.CentreX, -g.CentreY, g.CentreX, -g.CentreY], pen));
        if (StrokeGlyphs.ReadWord(strokes, _templates, region.Vertical) is not { } word)
            return [Unread(region.Box, region.Id, "", double.PositiveInfinity, [])];

        // Along-the-line coordinate, so an extent maps back to pixels and a cut can be placed.
        Func<double, double, double> along = word.Orientation.Rotation switch
        {
            0 => (x, y) => x, 180 => (x, y) => -x, 90 => (x, y) => y, _ => (x, y) => -y,
        };
        double start = strokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y));
        var asSeen = Words(region, word, along, start);
        var (cutStrokes, cutWord) = CutTouching(strokes, word, along, start, region.Vertical, pen);
        if (cutWord.Glyphs.Count == word.Glyphs.Count) return asSeen;
        // A cut stands where it reads more of the line — a typeset W cut in two is a t and a V, which say nothing — or as
        // much of it, nearer the templates: "kΩ" uncut is an m, poorly, and "1m" is grammatical.
        var cut = Words(region, cutWord, along, cutStrokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y)));
        int unreadCut = cut.Count(w => w.Reading is null), unreadSeen = asSeen.Count(w => w.Reading is null);
        return unreadCut < unreadSeen || (unreadCut == unreadSeen && Fit(cut) < Fit(asSeen)) ? cut : asSeen;

        static double Fit(IReadOnlyList<ImageWord> ws) =>
            ws.SelectMany(w => w.Glyphs).Select(g => g.Best.Distance).DefaultIfEmpty(double.PositiveInfinity).Average();
    }

    /// <summary>The words of a read line: whole when it reads whole, else token by token.</summary>
    private static IReadOnlyList<ImageWord> Words(TextRegion region, (IReadOnlyList<GlyphReading> Glyphs,
        IReadOnlyList<(double Left, double Right)> Extents, TextOrientation Orientation, double Height) word,
        Func<double, double, double> along, double start)
    {
        // Tokens: runs of glyphs with no space between them.
        var tokens = new List<(int From, int To)>();
        int from = 0;
        for (int i = 1; i <= word.Glyphs.Count; i++)
            if (i == word.Glyphs.Count || word.Extents[i].Left - word.Extents[i - 1].Right > SpaceGap)
            {
                tokens.Add((from, i));
                from = i;
            }

        // The whole line first, with its spaces; then word by word.
        if (Decode(word.Glyphs, tokens) is { } whole)
            return [Word(region, word, tokens[0].From, tokens[^1].To, whole.Reading, whole.Distance, along, start)];
        var result = new List<ImageWord>(tokens.Count);
        foreach (var t in tokens)
        {
            var one = Decode(word.Glyphs, [t]);
            result.Add(Word(region, word, t.From, t.To, one?.Reading, one?.Distance ?? Mean(word.Glyphs, t), along, start));
        }
        return result;
    }

    /// <summary>The word's stroke width over its cap height — the median height of its glyphs at least half as tall as
    /// the tallest, so an <c>=</c>'s bars and a full stop do not count; 0 when the width was not measured.</summary>
    internal static double Weight(TextRegion region)
    {
        if (region.InkWidth <= 0 || region.Glyphs.Count == 0) return 0;
        var heights = region.Glyphs.Select(g => region.Vertical ? g.Width : g.Height).ToList();
        int tallest = heights.Max();
        var full = heights.Where(h => 2 * h >= tallest).Order().ToList();
        return region.InkWidth / full[full.Count / 2];
    }

    /// <summary>
    /// <paramref name="xy"/> without the kink thinning leaves at a square stroke end: a final run no longer than a pen,
    /// and no longer than 2 px, that turns more than 35° off the stroke's next four times that, which must be straight. Small as it is, it widens the glyph's box, and the
    /// glyph is centred on its box — a typeset I with a one-pixel kink at its foot stood 0.08 cap heights off centre and
    /// read as a t. A stroke shorter than four pens is left whole, and only a FREE end is trimmed.
    /// </summary>
    internal static double[] TrimEndKinks(double[] xy, double pen, bool firstFree = true, bool lastFree = true)
    {
        int n = xy.Length / 2;
        if (n < 3) return xy;
        double total = 0;
        for (int i = 1; i < n; i++) total += Dist(xy, i - 1, i);
        if (total < 4 * pen) return xy;

        // The index the end at `from` keeps, walking by `step`: its first point at least a pen in, when the run up to it
        // turns off the stroke beyond.
        double kink = Math.Min(pen, 2);   // thinning curls a pixel or two at a square end, whatever the pen
        int Keep(int from, int step)
        {
            int j = from;
            double run = 0;
            while (j + step >= 0 && j + step < n && run < kink) { run += Dist(xy, j, j + step); j += step; }
            if (run > 1.5 * kink) return from;   // a long first segment: nothing to trim
            int m = j;
            double on = 0;
            while (m + step >= 0 && m + step < n && on < 4 * kink) { on += Dist(xy, m, m + step); m += step; }
            if (on < 4 * kink - 1) return from;
            // The run beyond must be straight: a kink ends a stem, not a bar turning into a curve or a diagonal.
            double cx = xy[2 * m] - xy[2 * j], cy = xy[2 * m + 1] - xy[2 * j + 1], len = Math.Sqrt(cx * cx + cy * cy);
            for (int k = j; k != m; k += step)
                if (Math.Abs((xy[2 * k] - xy[2 * j]) * cy - (xy[2 * k + 1] - xy[2 * j + 1]) * cx) / len > 0.75) return from;
            double ax = xy[2 * j] - xy[2 * from], ay = xy[2 * j + 1] - xy[2 * from + 1];
            double bx = xy[2 * m] - xy[2 * j], by = xy[2 * m + 1] - xy[2 * j + 1];
            double cos = (ax * bx + ay * by) / Math.Max(1e-12, Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)));
            return cos < Math.Cos(35 * Math.PI / 180) ? j : from;
        }
        int first = firstFree ? Keep(0, 1) : 0, last = lastFree ? Keep(n - 1, -1) : n - 1;
        if (first == 0 && last == n - 1) return xy;
        return xy[(2 * first)..(2 * last + 2)];
    }

    private static double Dist(double[] xy, int a, int b) =>
        Math.Sqrt((xy[2 * a] - xy[2 * b]) * (xy[2 * a] - xy[2 * b]) + (xy[2 * a + 1] - xy[2 * b + 1]) * (xy[2 * a + 1] - xy[2 * b + 1]));

    /// <summary>
    /// Two glyphs whose ink touches — a typeset <c>k</c> against an <c>Ω</c> — are one glyph to the matcher, and it
    /// reads as neither. Each glyph reading worse than <see cref="CutAbove"/> and at least <see cref="MinCutWidth"/> wide
    /// is cut across the line, at each twentieth from 30 % to 70 % of its width, its strokes stopped a pen short of the
    /// cut on each side; the cut whose two glyphs read best is kept when both read better than the whole did.
    /// </summary>
    private (List<SilkStroke>, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
             TextOrientation Orientation, double Height)) CutTouching(
        List<SilkStroke> strokes, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
                                   TextOrientation Orientation, double Height) word,
        Func<double, double, double> along, double start, bool vertical, double pen)
    {
        var tried = new HashSet<int>();
        for (int pass = 0; pass < 8; pass++)
        {
            int at = -1;
            for (int i = 0; i < word.Glyphs.Count; i++)
                if (!tried.Contains(i) && word.Glyphs[i].Best.Distance > CutAbove
                    && word.Extents[i].Right - word.Extents[i].Left >= MinCutWidth
                    && (at < 0 || word.Glyphs[i].Best.Distance > word.Glyphs[at].Best.Distance)) at = i;
            if (at < 0) break;
            tried.Add(at);

            var (l, r) = word.Extents[at];
            double whole = word.Glyphs[at].Best.Distance;
            (List<SilkStroke> Strokes, double Worse, double Sum, (IReadOnlyList<GlyphReading>, IReadOnlyList<(double, double)>, TextOrientation, double) Word)? best = null;
            for (double f = 0.3; f <= 0.7001; f += 0.05)
            {
                double c = start + (l + f * (r - l)) * word.Height;
                var cut = Cut(strokes, along, c, pen);
                if (StrokeGlyphs.ReadWord(cut, _templates!, vertical) is not { } w || w.Glyphs.Count != word.Glyphs.Count + 1) continue;
                double a = w.Glyphs[at].Best.Distance, b = w.Glyphs[at + 1].Best.Distance;
                if (best is null || a + b < best.Value.Sum) best = (cut, Math.Max(a, b), a + b, w);
            }
            if (best is not { } chosen || chosen.Worse >= whole) continue;
            strokes = chosen.Strokes;
            word = chosen.Word;
            tried = [.. tried.Select(i => i > at ? i + 1 : i)];
            start = strokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y));
        }
        return (strokes, word);
    }

    /// <summary><paramref name="strokes"/> with every piece within <paramref name="gap"/> of the line where
    /// <paramref name="along"/> is <paramref name="c"/> removed, each stroke split there.</summary>
    private static List<SilkStroke> Cut(List<SilkStroke> strokes, Func<double, double, double> along, double c, double gap)
    {
        var result = new List<SilkStroke>(strokes.Count + 2);
        foreach (var st in strokes)
        {
            var xy = st.Xy;
            if (xy.Length < 4)
            {
                if (Math.Abs(along(xy[0], xy[1]) - c) > gap) result.Add(st);
                continue;
            }
            var piece = new List<double>();
            void Flush()
            {
                if (piece.Count >= 2) result.Add(new SilkStroke([.. piece], st.Width));
                piece = [];
            }
            int Side(double a) => a < c - gap ? -1 : a > c + gap ? 1 : 0;
            for (int i = 0; i + 3 < xy.Length; i += 2)
            {
                double x0 = xy[i], y0 = xy[i + 1], x1 = xy[i + 2], y1 = xy[i + 3];
                double a0 = along(x0, y0), a1 = along(x1, y1);
                // The segment clipped to each side, in order along it.
                var cuts = new List<double> { 0, 1 };
                foreach (double edge in new[] { c - gap, c + gap })
                    if ((a0 - edge) * (a1 - edge) < 0) cuts.Add((edge - a0) / (a1 - a0));
                cuts.Sort();
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    double tm = 0.5 * (cuts[k] + cuts[k + 1]);
                    if (Side(a0 + tm * (a1 - a0)) == 0) { Flush(); continue; }
                    double xa = x0 + cuts[k] * (x1 - x0), ya = y0 + cuts[k] * (y1 - y0);
                    double xb = x0 + cuts[k + 1] * (x1 - x0), yb = y0 + cuts[k + 1] * (y1 - y0);
                    if (piece.Count == 0 || piece[^2] != xa || piece[^1] != ya) { Flush(); piece.AddRange([xa, ya]); }
                    piece.AddRange([xb, yb]);
                }
            }
            Flush();
        }
        return result;
    }

    private static IEnumerable<(double X, double Y)> Points(double[] xy)
    {
        for (int i = 0; i + 1 < xy.Length; i += 2) yield return (xy[i], xy[i + 1]);
    }

    private static double Mean(IReadOnlyList<GlyphReading> g, (int From, int To) t) =>
        Enumerable.Range(t.From, t.To - t.From).Average(i => g[i].Best.Distance);

    /// <summary>The word of glyphs <paramref name="from"/> … <paramref name="to"/>, its box in pixels.</summary>
    private static ImageWord Word(TextRegion region, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
                                  TextOrientation Orientation, double Height) w, int from, int to, WordReading? reading,
                                  double distance, Func<double, double, double> along, double start)
    {
        var glyphs = w.Glyphs.Skip(from).Take(to - from).ToList();
        string seen = new([.. glyphs.Select(g => g.Best.Char)]);
        var box = region.Box;
        if (from > 0 || to < w.Glyphs.Count)
        {
            // The token's extent along the line, back in pixels (y-up coordinates are the pixels' with y negated).
            double a0 = start + w.Extents[from].Left * w.Height, a1 = start + w.Extents[to - 1].Right * w.Height;
            (double lo, double hi) = w.Orientation.Rotation switch
            {
                0 => (a0, a1), 180 => (-a1, -a0), 90 => (-a1, -a0), _ => (a0, a1),
            };
            int l = (int)Math.Floor(lo) - 1, h = (int)Math.Ceiling(hi) + 1;
            box = region.Vertical
                ? box with { Top = Math.Max(box.Top, l), Bottom = Math.Min(box.Bottom, h) }
                : box with { Left = Math.Max(box.Left, l), Right = Math.Min(box.Right, h) };
        }
        return reading is null
            ? Unread(box, region.Id, seen, distance, glyphs)
            : new ImageWord(0, region.Id, box, reading, seen, distance, glyphs);
    }

    private static ImageWord Unread(PixelBox box, int region, string seen, double distance, IReadOnlyList<GlyphReading> glyphs) =>
        new(0, region, box, null, seen, distance, glyphs);

    /// <summary>
    /// The best grammatical reading of the glyphs in <paramref name="tokens"/> (spaces between them), or null: each
    /// glyph offers the characters within AS-10's runner-up margin of its best; the combinations are parsed, and the
    /// best-ranked one wins, then the one nearest its templates.
    /// </summary>
    internal static (WordReading Reading, double Distance)? Decode(IReadOnlyList<GlyphReading> glyphs, IReadOnlyList<(int From, int To)> tokens)
    {
        var all = tokens.SelectMany(t => Enumerable.Range(t.From, t.To - t.From)).ToList();
        if (all.Count == 0) return null;
        if (all.Any(i => glyphs[i].Best.Distance > MaxGlyphDistance)) return null;
        if (all.Average(i => glyphs[i].Best.Distance) > MaxWordDistance) return null;

        var spaceBefore = tokens.Skip(1).Select(t => t.From).ToHashSet();
        bool spaced = tokens.Count > 1;   // only a line set, or a value with its unit apart (0.5 pF), has a space
        var beam = new List<(string Text, double Score, bool[] Coerced)> { ("", 0, []) };
        foreach (int i in all)
        {
            var g = glyphs[i];
            double reach = Math.Max(g.Best.Distance / (1 - StrokeGlyphs.MinMargin), g.Best.Distance + MinReach) + 1e-9;
            var options = g.Ranked.Where(m => m.Distance <= reach).Take(MaxAlternatives).ToList();
            string sep = spaceBefore.Contains(i) ? " " : "";
            var next = new List<(string, double, bool[])>(beam.Count * options.Count);
            foreach (var (text, score, coerced) in beam)
                foreach (var m in options)
                    next.Add((text + sep + m.Char, score + m.Distance, [.. coerced, m.Distance > g.Best.Distance + 1e-9]));
            beam = [.. next.OrderBy(b => b.Item2).Take(Beam)];
        }

        (WordReading Reading, double Score, bool Known)? best = null;
        foreach (var (text, score, coerced) in beam)
        {
            if (ValueGrammar.Parse(text) is not { } r || !DesignatorRule(r, coerced)) continue;
            if (spaced && r.Class is not (WordClass.LineParameters or WordClass.Value)) continue;
            bool known = r.Class == WordClass.Designator && ValueGrammar.KnownPrefix(r.Text);
            if (best is not { } b || r.Rank < b.Reading.Rank
                || (r.Rank == b.Reading.Rank && (known && !b.Known || known == b.Known && score < b.Score)))
                best = (r, score, known);
        }
        return best is { } x ? (x.Reading, x.Score / all.Count) : null;
    }

    /// <summary>AS-10's designator rule: no letter where a digit fits better, and a digit where a letter fits better
    /// only after a known prefix.</summary>
    private static bool DesignatorRule(WordReading r, bool[] coerced)
    {
        if (r.Class != WordClass.Designator) return true;
        int letters = r.Text.TakeWhile(char.IsAsciiLetter).Count();
        for (int i = 0; i < coerced.Length; i++)
            if (coerced[i] && (i < letters || !ValueGrammar.KnownPrefix(r.Text))) return false;
        return true;
    }
}
