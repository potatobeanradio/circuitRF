// Opening, closing and despeckling — brief-img-1-raster-core.md R-im1-4.
//
// Erosion and dilation by a disc of radius r are thresholds of the exact distance transform: a pixel survives erosion
// when every pixel within r of it is set, and is set by dilation when any set pixel is within r. That gives a true disc
// at any radius, at the cost of one transform, rather than a square or a diamond from repeated 3×3 passes.

namespace CircuitRF.Design.Imaging;

public static class Morphology
{
    /// <summary>Pixels whose whole disc of <paramref name="radius"/> is set.</summary>
    public static BinaryImage Erode(BinaryImage mask, double radius, int maxThreads = 0)
    {
        var d2 = DistanceTransform.SquaredToBackground(mask, maxThreads);
        double r2 = radius * radius;
        var r = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < d2.Length; i++) r.Pixels[i] = d2[i] > r2 ? (byte)1 : (byte)0;
        return r;
    }

    /// <summary>Pixels within <paramref name="radius"/> of a set pixel.</summary>
    public static BinaryImage Dilate(BinaryImage mask, double radius, int maxThreads = 0)
    {
        var d2 = DistanceTransform.SquaredToForeground(mask, maxThreads);
        double r2 = radius * radius;
        var r = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < d2.Length; i++) r.Pixels[i] = d2[i] <= r2 ? (byte)1 : (byte)0;
        return r;
    }

    /// <summary>Erosion then dilation: removes what is narrower than the disc.</summary>
    public static BinaryImage Open(BinaryImage mask, double radius, int maxThreads = 0) =>
        Dilate(Erode(mask, radius, maxThreads), radius, maxThreads);

    /// <summary>Dilation then erosion: fills gaps narrower than the disc.</summary>
    public static BinaryImage Close(BinaryImage mask, double radius, int maxThreads = 0) =>
        Erode(Dilate(mask, radius, maxThreads), radius, maxThreads);

    /// <summary>The mask with every 8-connected component smaller than <paramref name="minArea"/> pixels removed.</summary>
    public static BinaryImage Despeckle(BinaryImage mask, int minArea)
    {
        var lab = Components.Label(mask);
        var r = mask.Clone();
        for (int i = 0; i < r.Pixels.Length; i++)
        {
            int l = lab.Labels[i];
            if (l > 0 && lab.Components[l - 1].Area < minArea) r.Pixels[i] = 0;
        }
        return r;
    }
}
