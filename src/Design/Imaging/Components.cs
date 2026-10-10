// Connected components — brief-img-1-raster-core.md R-im1-4.
//
// Two-pass labelling with union–find: 8-connected for the foreground, 4-connected for the background, the dual pair
// that keeps a diagonal stroke one piece and the background either side of it two. Labels are renumbered in raster
// order of each component's first pixel, so the numbering is a function of the picture alone.
//
// A component's holes are the background components (4-connected) that do not touch the border and whose first pixel's
// upper neighbour belongs to it: that pixel is on the hole's top edge, and what is above the top edge of a hole is the
// shape that encloses it — never a shape sitting inside the hole.

namespace CircuitRF.Design.Imaging;

/// <summary>One component.</summary>
/// <param name="Label">Its label in <see cref="ComponentLabels.Labels"/> (1-based; 0 is "not this kind").</param>
/// <param name="Left">Bounding box in pixels, inclusive.</param>
/// <param name="CentroidX">The mean of its pixel centres (pixel centres are at ½).</param>
/// <param name="Holes">Enclosed background components (foreground components only).</param>
/// <param name="TouchesBorder">Whether any of its pixels is on the picture's edge.</param>
public sealed record Component(int Label, int Area, int Left, int Top, int Right, int Bottom,
    double CentroidX, double CentroidY, int Holes, bool TouchesBorder);

/// <summary>A label per pixel and the components they name.</summary>
public sealed record ComponentLabels(int Width, int Height, int[] Labels, IReadOnlyList<Component> Components);

public static class Components
{
    /// <summary>Labels the set pixels of <paramref name="mask"/> (8-connected), or — with
    /// <paramref name="foreground"/> false — the unset ones (4-connected).</summary>
    public static ComponentLabels Label(BinaryImage mask, bool foreground = true)
    {
        var lab = LabelRaw(mask, foreground ? (byte)1 : (byte)0, eight: foreground, out int n);
        var comps = Describe(mask, lab, n);
        if (foreground && n > 0)
        {
            var bg = LabelRaw(mask, 0, eight: false, out int nb);
            var bgComps = Describe(mask, bg, nb);
            var holes = new int[n + 1];
            var seen = new bool[nb + 1];
            int w = mask.Width;
            for (int i = 0; i < bg.Length; i++)
            {
                int b = bg[i];
                if (b == 0 || seen[b]) continue;
                seen[b] = true;                                   // first pixel of this background component
                if (bgComps[b - 1].TouchesBorder) continue;
                int up = i - w;                                    // exists: a non-border component's first pixel is not on row 0
                if (up >= 0 && lab[up] > 0) holes[lab[up]]++;
            }
            comps = comps.Select(c => c with { Holes = holes[c.Label] }).ToList();
        }
        return new ComponentLabels(mask.Width, mask.Height, lab, comps);
    }

    private static int[] LabelRaw(BinaryImage mask, byte value, bool eight, out int count)
    {
        int w = mask.Width, h = mask.Height;
        var px = mask.Pixels;
        var lab = new int[w * h];
        var parent = new List<int> { 0 };
        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (px[i] != value) continue;
                int l = 0;
                void Take(int j)
                {
                    int lj = lab[j];
                    if (lj == 0) return;
                    if (l == 0) l = lj; else Union(l, lj);
                }
                if (x > 0) Take(i - 1);
                if (y > 0)
                {
                    Take(i - w);
                    if (eight)
                    {
                        if (x > 0) Take(i - w - 1);
                        if (x < w - 1) Take(i - w + 1);
                    }
                }
                if (l == 0)
                {
                    l = parent.Count;
                    parent.Add(l);
                }
                lab[i] = l;
            }
        // Renumber in raster order of first appearance.
        var final = new int[parent.Count];
        count = 0;
        for (int i = 0; i < lab.Length; i++)
        {
            if (lab[i] == 0) continue;
            int r = Find(lab[i]);
            if (final[r] == 0) final[r] = ++count;
            lab[i] = final[r];
        }
        return lab;
    }

    private static List<Component> Describe(BinaryImage mask, int[] lab, int n)
    {
        int w = mask.Width, h = mask.Height;
        var area = new int[n + 1];
        var l = new int[n + 1]; var t = new int[n + 1]; var r = new int[n + 1]; var b = new int[n + 1];
        var sx = new double[n + 1]; var sy = new double[n + 1];
        var border = new bool[n + 1];
        Array.Fill(l, int.MaxValue); Array.Fill(t, int.MaxValue);
        Array.Fill(r, -1); Array.Fill(b, -1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = lab[y * w + x];
                if (c == 0) continue;
                area[c]++;
                if (x < l[c]) l[c] = x;
                if (x > r[c]) r[c] = x;
                if (y < t[c]) t[c] = y;
                if (y > b[c]) b[c] = y;
                sx[c] += x + 0.5;
                sy[c] += y + 0.5;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border[c] = true;
            }
        var list = new List<Component>(n);
        for (int c = 1; c <= n; c++)
            list.Add(new Component(c, area[c], l[c], t[c], r[c], b[c], sx[c] / area[c], sy[c] / area[c], 0, border[c]));
        return list;
    }
}
