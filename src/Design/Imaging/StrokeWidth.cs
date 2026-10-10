// The stroke width of line art — brief-img-1-raster-core.md R-im1-5.
//
// The mode of 2·DT sampled on the skeleton: the one number the schematic reader scales every tolerance by. The
// distance transform measures to the nearest BACKGROUND PIXEL'S CENTRE, half a pixel beyond the stroke's edge, so the
// width at a skeleton pixel is 2·DT − 1: a 3 px stroke centred on a pixel row has background centres 2 px either side,
// and 2·2 − 1 = 3. Only path pixels (two skeleton neighbours) are sampled — a junction's DT measures the meeting, not a
// stroke.

namespace CircuitRF.Design.Imaging;

public static class StrokeWidth
{
    /// <summary>The stroke width of the foreground of <paramref name="mask"/>, in pixels; 0 when it has none.</summary>
    public static double Estimate(BinaryImage mask, int maxThreads = 0)
    {
        var skel = Skeleton.Thin(mask);
        SkeletonGraph.RemoveStaircaseCorners(skel);
        return Estimate(skel, DistanceTransform.Compute(mask, maxThreads));
    }

    /// <summary>The same from a skeleton and the mask's distance transform already in hand.</summary>
    public static double Estimate(BinaryImage skeleton, float[] dt)
    {
        var samples = new List<double>();
        int w = skeleton.Width, h = skeleton.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!skeleton[x, y]) continue;
                if (SkeletonGraph.Neighbours(skeleton, x, y) != 2) continue;
                samples.Add(Math.Max(1.0, 2.0 * dt[y * w + x] - 1.0));
            }
        if (samples.Count == 0)
            for (int i = 0; i < skeleton.Pixels.Length; i++)
                if (skeleton.Pixels[i] != 0) samples.Add(Math.Max(1.0, 2.0 * dt[i] - 1.0));
        return Mode(samples);
    }

    /// <summary>The mean of the samples within ½ px of the fullest ¼ px bin — a mode that is not quantised to the bin.</summary>
    internal static double Mode(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        var bins = new SortedDictionary<int, int>();
        foreach (var s in samples)
        {
            int b = (int)Math.Round(s * 4);
            bins[b] = bins.TryGetValue(b, out int c) ? c + 1 : 1;
        }
        int best = bins.First().Key, bc = -1;
        foreach (var (b, c) in bins) if (c > bc) { bc = c; best = b; }
        double centre = best / 4.0, sum = 0;
        int n = 0;
        foreach (var s in samples)
            if (Math.Abs(s - centre) <= 0.5) { sum += s; n++; }
        return sum / n;
    }
}
