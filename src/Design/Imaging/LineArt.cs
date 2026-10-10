// Dark strokes on a light ground → a mask — brief-img-1-raster-core.md R-im1-3.
//
// Sauvola's adaptive threshold: T = m·(1 + k·(s/R − 1)) over a window, from integral images of the grey level and its
// square. A ground that is dark (a dark-theme screenshot) is detected from the share of dark border pixels and the grey
// level inverted first, so the strokes are always the foreground.
//
// Sauvola alone reads the inside of a large solid dark area as background (m and s both fall to nothing there), so a
// pixel at or below AbsoluteDark is always foreground. Line art never has such an area; a filled symbol does.

namespace CircuitRF.Design.Imaging;

public sealed record LineArtOptions
{
    /// <summary>The Sauvola window side, in pixels (odd).</summary>
    public int Window { get; init; } = 25;

    /// <summary>Sauvola's k.</summary>
    public double K { get; init; } = 0.34;

    /// <summary>Sauvola's R, the grey level's dynamic range.</summary>
    public double R { get; init; } = 128;

    /// <summary>A grey level at or below this is foreground whatever the window says.</summary>
    public int AbsoluteDark { get; init; } = 64;

    /// <summary>Null detects the ground from the border; true or false states it.</summary>
    public bool? Invert { get; init; }

    public int MaxThreads { get; init; }
}

public static class LineArt
{
    /// <summary>The grey level of each pixel (Rec. 709 luma on the sRGB bytes), 0…255.</summary>
    public static byte[] Grey(RasterImage img)
    {
        var g = new byte[img.Width * img.Height];
        var p = img.Rgba;
        for (int i = 0, j = 0; i < g.Length; i++, j += 4)
            g[i] = (byte)Math.Clamp((int)Math.Round(0.2126 * p[j] + 0.7152 * p[j + 1] + 0.0722 * p[j + 2]), 0, 255);
        return g;
    }

    /// <summary>True when more than half the border pixels are dark — the strokes are then the light ones.</summary>
    public static bool DetectInverted(RasterImage img) => DetectInverted(Grey(img), img.Width, img.Height);

    internal static bool DetectInverted(byte[] grey, int w, int h)
    {
        long dark = 0, total = 0;
        for (int x = 0; x < w; x++)
        {
            dark += grey[x] < 128 ? 1 : 0;
            dark += grey[(h - 1) * w + x] < 128 ? 1 : 0;
            total += 2;
        }
        for (int y = 1; y < h - 1; y++)
        {
            dark += grey[y * w] < 128 ? 1 : 0;
            dark += grey[y * w + w - 1] < 128 ? 1 : 0;
            total += 2;
        }
        return dark * 2 > total;
    }

    /// <summary>The strokes of <paramref name="img"/> as foreground.</summary>
    public static BinaryImage Binarise(RasterImage img, LineArtOptions? options = null)
    {
        options ??= new LineArtOptions();
        int w = img.Width, h = img.Height;
        var g = Grey(img);
        if (options.Invert ?? DetectInverted(g, w, h))
            for (int i = 0; i < g.Length; i++) g[i] = (byte)(255 - g[i]);

        // Integral images, one row and one column larger.
        int iw = w + 1;
        var sum = new double[(long)iw * (h + 1)];
        var sq = new double[(long)iw * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rs = 0, rq = 0;
            for (int x = 0; x < w; x++)
            {
                double v = g[y * w + x];
                rs += v;
                rq += v * v;
                sum[(y + 1) * iw + x + 1] = sum[y * iw + x + 1] + rs;
                sq[(y + 1) * iw + x + 1] = sq[y * iw + x + 1] + rq;
            }
        }

        var mask = new BinaryImage(w, h);
        int half = Math.Max(1, options.Window / 2);
        double k = options.K, r = options.R;
        int dark = options.AbsoluteDark;
        Bands.For(h, options.MaxThreads, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int ya = Math.Max(0, y - half), yb = Math.Min(h, y + half + 1);
                for (int x = 0; x < w; x++)
                {
                    int xa = Math.Max(0, x - half), xb = Math.Min(w, x + half + 1);
                    double n = (double)(yb - ya) * (xb - xa);
                    double s1 = sum[yb * iw + xb] - sum[ya * iw + xb] - sum[yb * iw + xa] + sum[ya * iw + xa];
                    double s2 = sq[yb * iw + xb] - sq[ya * iw + xb] - sq[yb * iw + xa] + sq[ya * iw + xa];
                    double m = s1 / n, sd = Math.Sqrt(Math.Max(0, s2 / n - m * m));
                    double t = m * (1 + k * (sd / r - 1));
                    int v = g[y * w + x];
                    mask.Pixels[y * w + x] = v < t || v <= dark ? (byte)1 : (byte)0;
                }
            }
        });
        return mask;
    }
}
