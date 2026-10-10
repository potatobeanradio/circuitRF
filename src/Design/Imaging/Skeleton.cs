// Thinning — brief-img-1-raster-core.md R-im1-6.
//
// Zhang–Suen: two sub-iterations per pass, each deleting the boundary pixels that satisfy its conditions all at once
// (decided on the pass's input, applied after), until a pass deletes nothing. Deciding on the input rather than as it
// goes is what makes the result independent of the order pixels are visited in.
//
// Spurs are pruned on the graph (SkeletonGraph), where a branch's length is known; here the skeleton is only made thin.

namespace CircuitRF.Design.Imaging;

public static class Skeleton
{
    /// <summary>The one-pixel-wide centre lines of <paramref name="mask"/>.</summary>
    public static BinaryImage Thin(BinaryImage mask)
    {
        int w = mask.Width, h = mask.Height;
        var img = mask.Clone();
        var px = img.Pixels;
        var del = new List<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int sub = 0; sub < 2; sub++)
            {
                del.Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (px[i] == 0) continue;
                        // P2…P9 clockwise from north.
                        int p2 = img.At(x, y - 1) ? 1 : 0, p3 = img.At(x + 1, y - 1) ? 1 : 0, p4 = img.At(x + 1, y) ? 1 : 0;
                        int p5 = img.At(x + 1, y + 1) ? 1 : 0, p6 = img.At(x, y + 1) ? 1 : 0, p7 = img.At(x - 1, y + 1) ? 1 : 0;
                        int p8 = img.At(x - 1, y) ? 1 : 0, p9 = img.At(x - 1, y - 1) ? 1 : 0;
                        int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                        if (b < 2 || b > 6) continue;
                        int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0)
                              + (p5 == 0 && p6 == 1 ? 1 : 0) + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0)
                              + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                        if (a != 1) continue;
                        if (sub == 0 ? (p2 * p4 * p6 != 0 || p4 * p6 * p8 != 0) : (p2 * p4 * p8 != 0 || p2 * p6 * p8 != 0)) continue;
                        del.Add(i);
                    }
                foreach (int i in del) px[i] = 0;
                if (del.Count > 0) changed = true;
            }
        }
        return img;
    }
}
