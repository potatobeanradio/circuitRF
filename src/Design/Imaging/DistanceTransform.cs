// The exact Euclidean distance transform — brief-img-1-raster-core.md R-im1-5.
//
// Felzenszwalb & Huttenlocher's separable lower-envelope method: a 1-D squared-distance transform down every column,
// then along every row of the result. Exact (integer squared distances throughout), O(n), and each column and each row
// is independent, so the bands parallelise without changing a bit.
//
// Distances are between pixel CENTRES: a foreground pixel's value is the distance to the nearest background pixel's
// centre. Outside the picture counts as background — the edge of the picture is the paper.

namespace CircuitRF.Design.Imaging;

public static class DistanceTransform
{
    /// <summary>For each pixel set in <paramref name="mask"/>, the distance to the nearest unset pixel (or the picture's
    /// edge, one pixel beyond it); 0 for an unset pixel.</summary>
    public static float[] Compute(BinaryImage mask, int maxThreads = 0)
    {
        var d2 = SquaredToBackground(mask, maxThreads);
        var r = new float[d2.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (float)Math.Sqrt(d2[i]);
        return r;
    }

    /// <summary>The squared distances, exact integers.</summary>
    public static long[] SquaredToBackground(BinaryImage mask, int maxThreads = 0) => Squared(mask, foregroundIsTarget: false, maxThreads);

    /// <summary>For every pixel, the squared distance to the nearest SET pixel — what a dilation thresholds.</summary>
    public static long[] SquaredToForeground(BinaryImage mask, int maxThreads = 0) => Squared(mask, foregroundIsTarget: true, maxThreads);

    private const long Inf = long.MaxValue / 4;

    private static long[] Squared(BinaryImage mask, bool foregroundIsTarget, int maxThreads)
    {
        int w = mask.Width, h = mask.Height;
        // Padded by one pixel on every side. To-background: the pad is background (the paper). To-foreground: the pad
        // is nothing in particular, so it is not a target.
        int pw = w + 2, ph = h + 2;
        var f = new long[(long)pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                bool inside = x > 0 && y > 0 && x <= w && y <= h;
                bool set = inside && mask.Pixels[(y - 1) * w + x - 1] != 0;
                bool target = foregroundIsTarget ? set : !set;
                if (!inside && foregroundIsTarget) target = false;
                f[y * pw + x] = target ? 0 : Inf;
            }

        // Down the columns.
        Bands.For(pw, maxThreads, (x0, x1) =>
        {
            var col = new long[ph];
            var outp = new long[ph];
            var v = new int[ph];
            var z = new double[ph + 1];
            for (int x = x0; x < x1; x++)
            {
                for (int y = 0; y < ph; y++) col[y] = f[y * pw + x];
                Envelope(col, outp, v, z, ph);
                for (int y = 0; y < ph; y++) f[y * pw + x] = outp[y];
            }
        });
        // Along the rows.
        Bands.For(ph, maxThreads, (y0, y1) =>
        {
            var row = new long[pw];
            var outp = new long[pw];
            var v = new int[pw];
            var z = new double[pw + 1];
            for (int y = y0; y < y1; y++)
            {
                Array.Copy(f, (long)y * pw, row, 0, pw);
                Envelope(row, outp, v, z, pw);
                Array.Copy(outp, 0, f, (long)y * pw, pw);
            }
        });

        var r = new long[(long)w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                r[y * w + x] = Math.Min(f[(y + 1) * pw + x + 1], Inf);
        return r;
    }

    /// <summary>The 1-D squared-distance transform of <paramref name="f"/>: the lower envelope of the parabolas
    /// (q − p)² + f(p).</summary>
    private static void Envelope(long[] f, long[] d, int[] v, double[] z, int n)
    {
        int k = -1;
        for (int q = 0; q < n; q++)
        {
            if (f[q] >= Inf) continue;
            if (k < 0)
            {
                k = 0;
                v[0] = q;
                z[0] = double.NegativeInfinity;
                z[1] = double.PositiveInfinity;
                continue;
            }
            double s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + (long)q * q) - (f[p] + (long)p * p)) / (2.0 * (q - p));
                if (s > z[k]) break;
                k--;   // never below 0: z[0] is −∞
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        if (k < 0)
        {
            Array.Fill(d, Inf, 0, n);
            return;
        }
        int j = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[j + 1] < q) j++;
            long dq = q - v[j];
            d[q] = dq * dq + f[v[j]];
        }
    }
}
