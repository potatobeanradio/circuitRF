// The symbols of a schematic picture, as regions with the wire ends that reach them —
// brief-img-7-schematic-wires-and-regions.md R-im7-5 (hops), R-im7-6, R-im7-7.
//
// A symbol region is what is left when the words and the wires are taken away: the residue's pieces, grown by Grow w,
// then MERGED when two face each other across a gap shorter than the larger one's extent along the faces — which
// joins a capacitor's two plates, a ground's bars and an inductor's separate humps into one region. Two pieces with a
// wire in the gap between them are never merged: that gap is a lead, and the pieces are two parts.
//
// A region's attachments are the wire ends stopping within Attach w of it, each with the direction its wire arrives
// in. Three shapes of region are not symbols:
//  * a HOP — two attachments, collinear and opposite, a few w apart, with another wire passing between them: the two
//    wire halves are one wire, crossing the other without connecting (R-im7-5);
//  * a SUPPLY MARK — one attachment, and a short bar across the end or an arrow whose apex the wire reaches (R-im7-7);
//  * a DECORATION — no attachment at all (a logo, a frame, a title block's lines), reported and dropped.
// A word at a dangling end, in line with its wire, is that wire's NET LABEL.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A wire end reaching a symbol region, and the unit direction its wire arrives in.</summary>
public sealed record SymbolAttachment(int Node, double X, double Y, double DirX, double DirY);

/// <summary>One symbol on the picture, not yet named (IM-8 names it).</summary>
/// <param name="Mask">The region's own pixels, cropped to <paramref name="Box"/>.</param>
public sealed record SymbolRegion(int Id, PixelBox Box, int Area, IReadOnlyList<SymbolAttachment> Attachments, BinaryImage Mask);

/// <summary>A word naming the net of the wire ending at <paramref name="Node"/> (R-im7-7).</summary>
/// <param name="Text">The word's <see cref="TextRegion.Id"/>.</param>
public sealed record NetLabel(int Node, int Text);

public enum SupplyShape { Bar, Arrow }

/// <summary>A supply mark at the end of the wire ending at <paramref name="Node"/>; the word beside it names its net.</summary>
public sealed record SupplyMark(int Node, PixelBox Box, SupplyShape Shape, int? Text);

/// <summary>A residue piece group before it is known what it is.</summary>
internal sealed record RawRegion(PixelBox Box, List<int> Pixels);

internal sealed record AttachResult(List<SymbolRegion> Regions, List<PixelBox> Decorations, List<NetLabel> Labels,
                                    List<SupplyMark> Supplies, List<int> Dangling);

internal static class SymbolRegions
{
    /// <summary>The residue: <paramref name="mask"/> without the wires of <paramref name="wires"/> and the junction
    /// dots on them, and without specks.</summary>
    public static BinaryImage Residue(BinaryImage mask, WireGraph wires, IReadOnlyList<JunctionBlob> blobs, double w)
    {
        int W = mask.Width, H = mask.Height;
        var r = mask.Clone();
        foreach (var s in wires.Segments)
        {
            double rad = Math.Max(w, s.Width) / 2 + 1;
            // At a bare end the stroke's own cap is wire too; at any other end the next stroke starts there.
            double before = wires.Nodes[s.From].IsEnd && wires.Nodes[s.From].Free ? rad : 0;
            double after = wires.Nodes[s.To].IsEnd && wires.Nodes[s.To].Free ? rad : 0;
            double pad = 2 * rad;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(s.A.x, s.B.x) - pad)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(s.A.x, s.B.x) + pad));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(s.A.y, s.B.y) - pad)), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(s.A.y, s.B.y) + pad));
            double len = s.Length;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var (along, perp) = WireGraph.Local(s.A, s.B, new PointD(x + 0.5, y + 0.5));
                    if (along >= -before && along <= len + after && Math.Abs(perp) <= rad) r.Pixels[y * W + x] = 0;
                }
        }
        foreach (var b in blobs)
        {
            if (!wires.Nodes.Any(n => n.Degree >= 3 && WireGraph.Dist(new PointD(n.X, n.Y), b.Centre) <= b.Radius + w)) continue;
            double rad = b.Radius + 1;
            for (int y = Math.Max(0, (int)(b.Centre.y - rad)); y <= Math.Min(H - 1, (int)(b.Centre.y + rad)); y++)
                for (int x = Math.Max(0, (int)(b.Centre.x - rad)); x <= Math.Min(W - 1, (int)(b.Centre.x + rad)); x++)
                    if (WireGraph.Dist(new PointD(x + 0.5, y + 0.5), b.Centre) <= rad) r.Pixels[y * W + x] = 0;
        }
        return Morphology.Despeckle(r, Math.Max(4, (int)Math.Round(1.5 * w * w)));
    }

    /// <summary>The residue's pieces, grown and merged into regions (R-im7-6).</summary>
    public static List<RawRegion> Group(BinaryImage residue, WireGraph wires, double w, SchematicImageOptions o)
    {
        int W = residue.Width;
        var grown = Morphology.Dilate(residue, o.Grow * w, o.MaxThreads);
        var lab = Components.Label(grown);
        int n = lab.Components.Count;
        var pixels = new List<int>[n];
        for (int k = 0; k < n; k++) pixels[k] = [];
        for (int i = 0; i < residue.Pixels.Length; i++)
            if (residue.Pixels[i] != 0) pixels[lab.Labels[i] - 1].Add(i);
        var boxes = pixels.Select(p => p.Count == 0 ? default : BoxOf(p, W)).ToArray();
        var alive = pixels.Select(p => p.Count > 0).ToArray();

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n && !changed; i++)
            {
                if (!alive[i]) continue;
                for (int j = i + 1; j < n && !changed; j++)
                {
                    if (!alive[j] || !Facing(boxes[i], boxes[j], wires)) continue;
                    pixels[i].AddRange(pixels[j]);
                    boxes[i] = boxes[i].Union(boxes[j]);
                    alive[j] = false;
                    changed = true;
                }
            }
        }
        var r = new List<RawRegion>();
        for (int k = 0; k < n; k++)
            if (alive[k]) r.Add(new RawRegion(boxes[k], [.. pixels[k].OrderBy(i => i)]));
        return [.. r.OrderBy(x => x.Box.Top).ThenBy(x => x.Box.Left)];
    }

    /// <summary>Two boxes facing each other across a gap shorter than the larger one's extent along the faces, with no
    /// wire in the gap.</summary>
    private static bool Facing(PixelBox a, PixelBox b, WireGraph wires)
    {
        int ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + 1;
        int oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + 1;
        if (ox >= 0.5 * Math.Min(a.Width, b.Width))
        {
            int gap = Math.Max(b.Top - a.Bottom - 1, a.Top - b.Bottom - 1);
            if (gap < 0 || gap >= Math.Max(a.Width, b.Width)) return false;
            int top = Math.Min(a.Bottom, b.Bottom) + 1, bottom = Math.Max(a.Top, b.Top);
            return !WireIn(Math.Max(a.Left, b.Left), top, Math.Min(a.Right, b.Right) + 1, bottom, wires);
        }
        if (oy >= 0.5 * Math.Min(a.Height, b.Height))
        {
            int gap = Math.Max(b.Left - a.Right - 1, a.Left - b.Right - 1);
            if (gap < 0 || gap >= Math.Max(a.Height, b.Height)) return false;
            int left = Math.Min(a.Right, b.Right) + 1, right = Math.Max(a.Left, b.Left);
            return !WireIn(left, Math.Max(a.Top, b.Top), right, Math.Min(a.Bottom, b.Bottom) + 1, wires);
        }
        return false;
    }

    /// <summary>Whether any wire crosses the rectangle [x0, x1] × [y0, y1] (pixel edges).</summary>
    private static bool WireIn(double x0, double y0, double x1, double y1, WireGraph wires)
    {
        if (x1 <= x0 || y1 <= y0) return false;
        foreach (var s in wires.Segments)
        {
            // Liang–Barsky.
            double t0 = 0, t1 = 1, dx = s.B.x - s.A.x, dy = s.B.y - s.A.y;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }
            if (Clip(-dx, s.A.x - x0) && Clip(dx, x1 - s.A.x) && Clip(-dy, s.A.y - y0) && Clip(dy, y1 - s.A.y) && t0 <= t1)
                return true;
        }
        return false;
    }

    /// <summary>The regions that are hops (R-im7-5), as joins of the two wire halves, and which regions they were. The
    /// wire hopped cuts a hop's arc in two where it passes under its apex, so a hop is one region with both ends or two
    /// regions with one end each — never merged across that wire, which is the rule that keeps two parts apart.</summary>
    public static (List<HopJoin> Hops, HashSet<int> Regions) Hops(List<RawRegion> regions, int width, WireGraph wires,
                                                                    double w, SchematicImageOptions o)
    {
        var hops = new List<HopJoin>();
        var used = new HashSet<int>();
        var at = Attachments(regions, width, wires, w, o);
        var tries = new List<(int A, int B, SymbolAttachment P, SymbolAttachment Q)>();
        for (int k = 0; k < regions.Count; k++)
            if (at[k].Count == 2) tries.Add((k, k, at[k][0], at[k][1]));
        for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
                if (at[i].Count == 1 && at[j].Count == 1)
                {
                    var u = regions[i].Box.Union(regions[j].Box);
                    if (u.Width <= 10 * w + 2 && u.Height <= 10 * w + 2) tries.Add((i, j, at[i][0], at[j][0]));
                }
        foreach (var (ra, rb, p, q) in tries)
        {
            if (used.Contains(ra) || used.Contains(rb)) continue;
            if (Hop(p, q, wires, w) is not { } hop) continue;
            hops.Add(hop);
            used.Add(ra);
            used.Add(rb);
        }
        return (hops, used);
    }

    /// <summary>Two wire ends facing each other a few w apart, on one line, with another wire passing between them.</summary>
    private static HopJoin? Hop(SymbolAttachment p, SymbolAttachment q, WireGraph wires, double w)
    {
        if (p.DirX * q.DirX + p.DirY * q.DirY > -0.95) return null;
        var (a, b) = (new PointD(p.X, p.Y), new PointD(q.X, q.Y));
        double d = WireGraph.Dist(a, b);
        if (d < 2 * w || d > 10 * w) return null;
        if (Math.Abs(WireGraph.Local(a, new PointD(a.x + p.DirX, a.y + p.DirY), b).Perp) > w) return null;
        var sa = wires.SegmentAt(p.Node).Id;
        var sb = wires.SegmentAt(q.Node).Id;
        foreach (var s in wires.Segments)
        {
            if (s.Id == sa || s.Id == sb) continue;
            if (Intersect(a, b, s.A, s.B) is { } x) return new HopJoin(a, b, x);
        }
        return null;
    }

    /// <summary>The attachments, supply marks, net labels and decorations, against the final wires.</summary>
    public static AttachResult Attach(List<RawRegion> regions, int width, WireGraph wires,
                                      IReadOnlyList<TextRegion> words, double w, SchematicImageOptions o)
    {
        var at = Attachments(regions, width, wires, w, o);
        var result = new AttachResult([], [], [], [], []);
        var reached = new HashSet<int>();
        for (int k = 0; k < regions.Count; k++)
        {
            var reg = regions[k];
            if (at[k].Count == 0)
            {
                result.Decorations.Add(reg.Box);
                continue;
            }
            foreach (var a in at[k]) reached.Add(a.Node);
            if (at[k].Count == 1 && Supply(reg.Box, at[k][0], w) is { } shape)
            {
                result.Supplies.Add(new SupplyMark(at[k][0].Node, reg.Box, shape, Beside(reg.Box, words, 3 * w)));
                continue;
            }
            var mask = new BinaryImage(reg.Box.Width, reg.Box.Height);
            foreach (int i in reg.Pixels) mask.Pixels[(i / width - reg.Box.Top) * mask.Width + (i % width - reg.Box.Left)] = 1;
            result.Regions.Add(new SymbolRegion(result.Regions.Count, reg.Box, reg.Pixels.Count, at[k], mask));
        }

        foreach (var end in wires.Ends)
        {
            if (reached.Contains(end.Id)) continue;
            var (dx, dy) = wires.Incoming(end.Id);
            int best = -1;
            double bd = o.LabelGap * w + w / 2 + 1;
            foreach (var t in words)
            {
                double dist = t.Box.Distance(end.X, end.Y);
                if (dist > bd) continue;
                // In line with the wire: its line passes through the word's box, widened across by 1.5 w.
                double pad = 1.5 * w;
                bool inLine = Math.Abs(dx) >= Math.Abs(dy)
                    ? end.Y >= t.Box.Top - pad && end.Y <= t.Box.Bottom + 1 + pad
                    : end.X >= t.Box.Left - pad && end.X <= t.Box.Right + 1 + pad;
                if (!inLine) continue;
                bd = dist;
                best = t.Id;
            }
            if (best >= 0) result.Labels.Add(new NetLabel(end.Id, best));
            else result.Dangling.Add(end.Id);
        }
        return result;
    }

    /// <summary>For each region, the wire ends stopping within Attach w of one of its pixels — each end going to the
    /// region nearest it.</summary>
    private static List<SymbolAttachment>[] Attachments(List<RawRegion> regions, int width, WireGraph wires, double w,
                                                        SchematicImageOptions o)
    {
        var r = new List<SymbolAttachment>[regions.Count];
        for (int k = 0; k < regions.Count; k++) r[k] = [];
        if (regions.Count == 0) return r;
        int height = regions.Max(g => g.Box.Bottom) + 1;
        var owner = new Dictionary<int, int>();
        for (int k = 0; k < regions.Count; k++) foreach (int i in regions[k].Pixels) owner[i] = k;
        double reach = o.Attach * w + w / 2 + 1;
        foreach (var end in wires.Ends)
        {
            int best = -1;
            double bd = reach * reach;
            int x0 = (int)Math.Floor(end.X - reach), x1 = (int)Math.Ceiling(end.X + reach);
            int y0 = (int)Math.Floor(end.Y - reach), y1 = (int)Math.Ceiling(end.Y + reach);
            for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++)
                {
                    if (!owner.TryGetValue(y * width + x, out int k)) continue;
                    double d2 = (x + 0.5 - end.X) * (x + 0.5 - end.X) + (y + 0.5 - end.Y) * (y + 0.5 - end.Y);
                    if (d2 < bd || (d2 == bd && k < best)) { bd = d2; best = k; }
                }
            if (best < 0) continue;
            var (dx, dy) = wires.Incoming(end.Id);
            r[best].Add(new SymbolAttachment(end.Id, end.X, end.Y, dx, dy));
        }
        return r;
    }

    /// <summary>A supply mark's shape, or null: a short bar across the end, or an arrow whose apex the wire reaches.</summary>
    private static SupplyShape? Supply(PixelBox box, SymbolAttachment a, double w)
    {
        bool horizontal = Math.Abs(a.DirX) >= Math.Abs(a.DirY);
        double alongMin = horizontal ? box.Left : box.Top, alongMax = horizontal ? box.Right + 1 : box.Bottom + 1;
        double acrossMin = horizontal ? box.Top : box.Left, acrossMax = horizontal ? box.Bottom + 1 : box.Right + 1;
        double end = horizontal ? a.X : a.Y, axis = horizontal ? a.Y : a.X;
        double dir = horizontal ? Math.Sign(a.DirX) : Math.Sign(a.DirY);
        double along = alongMax - alongMin, across = acrossMax - acrossMin;
        if (Math.Abs((acrossMin + acrossMax) / 2 - axis) > 1.5 * w) return null;
        if (along <= 2 * w + 2 && across >= 3 * w && across <= 16 * w) return SupplyShape.Bar;
        // An arrow: the box lies behind the end, the wire reaching its apex.
        double front = dir > 0 ? alongMax : -alongMin, back = dir > 0 ? alongMin : -alongMax, e = dir * end;
        if (across >= 2 * w && across <= 10 * w && along >= 2 * w && along <= 10 * w && front <= e + 1.5 * w && back < e - w)
            return SupplyShape.Arrow;
        return null;
    }

    /// <summary>The word nearest <paramref name="box"/> within <paramref name="within"/> pixels, or null.</summary>
    private static int? Beside(PixelBox box, IReadOnlyList<TextRegion> words, double within)
    {
        int? best = null;
        double bd = within;
        foreach (var t in words)
        {
            double d = box.Distance(t.Box);
            if (d <= bd) { bd = d; best = t.Id; }
        }
        return best;
    }

    private static PixelBox BoxOf(List<int> pixels, int width)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (int i in pixels)
        {
            int x = i % width, y = i / width;
            l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
        }
        return new PixelBox(l, t, r, b);
    }

    private static PointD? Intersect(PointD a, PointD b, PointD c, PointD d)
    {
        double rx = b.x - a.x, ry = b.y - a.y, sx = d.x - c.x, sy = d.y - c.y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return null;
        double t = ((c.x - a.x) * sy - (c.y - a.y) * sx) / den;
        double u = ((c.x - a.x) * ry - (c.y - a.y) * rx) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return null;
        return new PointD(a.x + t * rx, a.y + t * ry);
    }
}
