// The words of a schematic picture, found and taken out before the wires are traced —
// brief-img-7-schematic-wires-and-regions.md R-im7-3 (overview D10).
//
// A word is found by its SHAPE, not read: IM-9 reads it. The candidates are the small pieces of ink left when every long
// straight run is set aside — a run longer than the tallest glyph is line work, never a letter — so a label touching a
// wire is still a piece of its own. The pieces are grouped into lines by AS-10's own line grouping
// (StrokeGlyphs.Groups, fed boxes rather than pen strokes): pieces of one height, side by side on one band, offered
// whole and split at the widest gap.
//
// A word's pixels are removed before the wires are traced, so a label touching a wire does not become a wire stub. A
// group that touches line work along more than TextContact w — or at two separate places, as a capacitor's two plates
// on their leads or an inductor's humps between theirs do — is more likely a symbol detail, and stays; unless what is
// left without its touching pieces is still two glyphs, as a supply's name beside the two halves of its bar is.

using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>One of the shorter readings of a word: its box and which of the word's glyphs it holds.</summary>
public sealed record TextRegionSplit(PixelBox Box, IReadOnlyList<int> Glyphs);

/// <summary>A word on the picture (R-im7-3).</summary>
/// <param name="Vertical">It reads along y (text turned 90°).</param>
/// <param name="Glyphs">Each glyph's box, in reading order.</param>
/// <param name="Strokes">The word's skeleton, as polylines in picture pixels — what IM-9 reads.</param>
/// <param name="Splits">The word split at its widest gap, and those halves split again — the other readings offered.</param>
public sealed record TextRegion(int Id, PixelBox Box, bool Vertical, IReadOnlyList<PixelBox> Glyphs,
    IReadOnlyList<PathD> Strokes, IReadOnlyList<TextRegionSplit> Splits);

/// <summary>A word-shaped group kept as part of a symbol because it touches line work.</summary>
/// <param name="Contact">The longest contact, in w.</param>
/// <param name="Places">At how many separate places it touches.</param>
public sealed record KeptText(PixelBox Box, double Contact, int Places);

internal sealed record TextRegionsResult(List<TextRegion> Words, List<KeptText> Kept, BinaryImage Removed);

internal static class TextRegions
{
    public static TextRegionsResult Find(BinaryImage mask, double w, SchematicImageOptions o)
    {
        int width = mask.Width, height = mask.Height;
        int longRun = (int)Math.Ceiling(o.MaxTextHeight * w) + 1;
        var line = LongRuns(mask, longRun);
        var rest = mask.Clone();
        for (int i = 0; i < rest.Pixels.Length; i++) if (line.Pixels[i] != 0) rest.Pixels[i] = 0;

        var lab = Components.Label(rest);
        double maxH = o.MaxTextHeight * w + 2;
        var cands = lab.Components
            .Where(c => c.Bottom - c.Top + 1 <= maxH && c.Right - c.Left + 1 <= maxH)
            .ToList();
        var pixels = new Dictionary<int, List<int>>();
        foreach (var c in cands) pixels[c.Label] = [];
        for (int i = 0; i < lab.Labels.Length; i++)
            if (lab.Labels[i] > 0 && pixels.TryGetValue(lab.Labels[i], out var list)) list.Add(i);

        var words = new List<TextRegion>();
        var kept = new List<KeptText>();
        var removed = new BinaryImage(width, height);
        var used = new HashSet<int>();                       // indices into cands

        foreach (bool vertical in new[] { false, true })
        {
            var pool = Enumerable.Range(0, cands.Count).Where(k => !used.Contains(k)).ToList();
            var units = new List<StrokeGlyphs.Unit>(pool.Count);
            foreach (int k in pool)
            {
                var c = cands[k];
                var u = new StrokeGlyphs.Unit();
                u.Add(k, [c.Left, c.Top, c.Right + 1, c.Bottom + 1]);
                units.Add(u);
            }
            var groups = StrokeGlyphs.Groups(units, vertical)
                .Select(g => g.Select(i => pool[i]).ToList())
                .ToList();
            var sets = groups.Select(g => g.ToHashSet()).ToList();

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                bool split = false;
                for (int gj = 0; gj < groups.Count && !split; gj++)
                    if (gj != gi && sets[gj].Count > sets[gi].Count && sets[gi].IsSubsetOf(sets[gj])) split = true;
                if (split || g.Any(used.Contains)) continue;

                var across = g.Select(k => Across(cands[k], vertical)).OrderBy(v => v).ToList();
                double h = across[across.Count / 2];
                if (h < o.MinTextHeight * w || h > maxH) continue;

                var ordered = g.OrderBy(k => vertical ? cands[k].Top : cands[k].Left).ThenBy(k => k).ToList();
                var box = Box(cands[ordered[0]]);
                foreach (int k in ordered) box = box.Union(Box(cands[k]));

                var (contact, places) = Contact(ordered.SelectMany(k => pixels[cands[k].Label]), line, width);
                foreach (int k in g) used.Add(k);
                if (contact > o.TextContact * w || places >= 2)
                {
                    // The pieces touching line work may be a symbol's — a supply bar's two halves beside its name —
                    // and the rest still a word; it is one when two glyph-height pieces remain.
                    var touching = ordered.Where(k => Contact(pixels[cands[k].Label], line, width).Places > 0).ToHashSet();
                    var clear = ordered.Where(k => !touching.Contains(k)).ToList();
                    bool word = clear.Count(k => Across(cands[k], vertical) >= o.MinTextHeight * w) >= 2;
                    var keptBox = word ? BoxOf(touching.Select(k => cands[k])) : box;
                    var (kc, kp) = word ? Contact(touching.SelectMany(k => pixels[cands[k].Label]), line, width) : (contact, places);
                    kept.Add(new KeptText(keptBox, kc / w, kp));
                    if (!word) continue;
                    ordered = clear;
                    box = BoxOf(clear.Select(k => cands[k]));
                }

                foreach (int k in ordered)
                    foreach (int i in pixels[cands[k].Label]) removed.Pixels[i] = 1;
                var mine = ordered.ToHashSet();
                var glyphs = ordered.Select(k => Box(cands[k])).ToList();
                var splits = new List<TextRegionSplit>();
                for (int gj = 0; gj < groups.Count; gj++)
                {
                    if (gj == gi || !sets[gj].IsSubsetOf(mine) || sets[gj].Count == mine.Count) continue;
                    var idx = ordered.Select((k, n) => (k, n)).Where(p => sets[gj].Contains(p.k)).Select(p => p.n).ToList();
                    var sb = glyphs[idx[0]];
                    foreach (int n in idx) sb = sb.Union(glyphs[n]);
                    splits.Add(new TextRegionSplit(sb, idx));
                }
                words.Add(new TextRegion(0, box, vertical, glyphs,
                    Strokes(ordered.SelectMany(k => pixels[cands[k].Label]), box, width), splits));
            }
        }

        // Reading order: top to bottom, then left to right.
        words = words.OrderBy(t => t.Box.Top).ThenBy(t => t.Box.Left).Select((t, i) => t with { Id = i }).ToList();
        kept = kept.OrderBy(t => t.Box.Top).ThenBy(t => t.Box.Left).ToList();
        return new TextRegionsResult(words, kept, removed);
    }

    private static PixelBox Box(Component c) => new(c.Left, c.Top, c.Right, c.Bottom);

    private static PixelBox BoxOf(IEnumerable<Component> cs)
    {
        PixelBox? b = null;
        foreach (var c in cs) b = b is { } x ? x.Union(Box(c)) : Box(c);
        return b ?? default;
    }

    private static double Across(Component c, bool vertical) => vertical ? c.Right - c.Left + 1 : c.Bottom - c.Top + 1;

    /// <summary>Every pixel on a horizontal or vertical run of at least <paramref name="min"/> pixels.</summary>
    internal static BinaryImage LongRuns(BinaryImage mask, int min)
    {
        int w = mask.Width, h = mask.Height;
        var r = new BinaryImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w;)
            {
                if (!mask[x, y]) { x++; continue; }
                int s = x;
                while (x < w && mask[x, y]) x++;
                if (x - s >= min) for (int k = s; k < x; k++) r.Pixels[y * w + k] = 1;
            }
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h;)
            {
                if (!mask[x, y]) { y++; continue; }
                int s = y;
                while (y < h && mask[x, y]) y++;
                if (y - s >= min) for (int k = s; k < y; k++) r.Pixels[k * w + x] = 1;
            }
        return r;
    }

    /// <summary>The longest contact of <paramref name="pixels"/> with <paramref name="line"/>, pixels, and at how many
    /// separate places they touch.</summary>
    private static (double Longest, int Places) Contact(IEnumerable<int> pixels, BinaryImage line, int width)
    {
        var touching = new List<(int X, int Y)>();
        foreach (int i in pixels)
        {
            int x = i % width, y = i / width;
            bool t = false;
            for (int dy = -1; dy <= 1 && !t; dy++)
                for (int dx = -1; dx <= 1 && !t; dx++)
                    if ((dx | dy) != 0 && line.At(x + dx, y + dy)) t = true;
            if (t) touching.Add((x, y));
        }
        if (touching.Count == 0) return (0, 0);

        // Places: touching pixels within two pixels of each other are one place.
        var parent = Enumerable.Range(0, touching.Count).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        for (int a = 0; a < touching.Count; a++)
            for (int b = a + 1; b < touching.Count; b++)
                if (Math.Abs(touching[a].X - touching[b].X) <= 2 && Math.Abs(touching[a].Y - touching[b].Y) <= 2)
                    parent[Find(b)] = Find(a);
        double longest = 0;
        int places = 0;
        foreach (var g in Enumerable.Range(0, touching.Count).GroupBy(Find))
        {
            places++;
            int x0 = g.Min(i => touching[i].X), x1 = g.Max(i => touching[i].X);
            int y0 = g.Min(i => touching[i].Y), y1 = g.Max(i => touching[i].Y);
            longest = Math.Max(longest, Math.Max(x1 - x0 + 1, y1 - y0 + 1));
        }
        return (longest, places);
    }

    /// <summary>The skeleton of a word's pixels, as polylines in picture pixels.</summary>
    private static List<PathD> Strokes(IEnumerable<int> pixels, PixelBox box, int width)
    {
        int ox = box.Left - 1, oy = box.Top - 1;
        var crop = new BinaryImage(box.Width + 2, box.Height + 2);
        foreach (int i in pixels) crop.Pixels[(i / width - oy) * crop.Width + (i % width - ox)] = 1;
        var g = SkeletonGraph.Build(crop, new SkeletonGraphOptions { MaxThreads = 1 });
        var r = new List<PathD>(g.Edges.Count);
        foreach (var e in g.Edges)
        {
            var p = new PathD(e.Polyline.Count);
            foreach (var v in e.Polyline) p.Add(new PointD(v.x + ox, v.y + oy));
            r.Add(p);
        }
        return r;
    }
}
