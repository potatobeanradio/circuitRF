// Colour separation — brief-img-1-raster-core.md R-im1-2 (D7, D17).
//
// k-means++ in CIELAB with a FIXED seed on a deterministic stride sample, k chosen at the elbow of the within-cluster
// error, clusters closer than ΔE 10 merged. Two refinements make it behave on rendered pictures, both recorded in
// docs/design/image-to-circuit.md §4.2:
//
//   - The error the elbow reads treats an EDGE pixel (one whose colour jumps against a 4-neighbour) as a MIX of two
//     cluster colours, scored by its distance to the segment between them. An anti-aliased edge between red and white
//     is pink, and without this a fourth "pink" cluster would always look worth adding. A FLAT pixel is scored by its
//     distance to the nearest centre, so a flat grey region between black and white is still a colour of its own.
//   - The final centres are the means of the flat samples alone, so anti-aliasing does not pull a colour toward the
//     background.
//
// Each pixel is then described as a mix of two cluster colours: the nearest, and the one whose segment with it passes
// closest to the pixel. The mix fraction is measured in the sRGB bytes the picture was blended in, which is what makes a
// coverage of 0.5 sit on the drawn edge.

namespace CircuitRF.Design.Imaging;

/// <summary>One separated colour.</summary>
/// <param name="Index">Its index in <see cref="ColourClusterSet.Clusters"/> and in <see cref="ColourClusterSet.Coverage"/>.</param>
/// <param name="Rgb">The colour as 0xRRGGBB.</param>
/// <param name="Share">The fraction of the picture's pixels it covers most of.</param>
/// <param name="BorderShare">The same fraction over the picture's border pixels alone — the background is the cluster
/// with the largest.</param>
public sealed record ColourCluster(int Index, Lab Lab, int Rgb, double Share, double BorderShare)
{
    public string Hex => $"#{Rgb:x6}";
}

/// <summary>Options for <see cref="ColourClusters.Find"/>.</summary>
public sealed record ColourClusterOptions
{
    /// <summary>The PRNG seed. Fixed so the same picture always separates the same way.</summary>
    public ulong Seed { get; init; } = 0x5EED_C010_0001UL;

    /// <summary>At most this many pixels are sampled for the clustering.</summary>
    public int MaxSamples { get; init; } = 250_000;

    /// <summary>Clusters closer than this ΔE are merged.</summary>
    public double MergeDeltaE { get; init; } = 10.0;

    /// <summary>A pixel whose colour differs from a 4-neighbour by more than this ΔE is an edge pixel.</summary>
    public double EdgeDeltaE { get; init; } = 8.0;

    /// <summary>Mean squared ΔE at or below which a clustering is taken as complete (noise level).</summary>
    public double NoiseFloor2 { get; init; } = 4.0;

    /// <summary>0 = the runtime's choice; the result does not depend on it.</summary>
    public int MaxThreads { get; init; }
}

/// <summary>The clusters of one picture, and every pixel's two-colour mix.</summary>
public sealed class ColourClusterSet
{
    internal ColourClusterSet(int width, int height, IReadOnlyList<ColourCluster> clusters, byte[] a, byte[] b, byte[] t)
    {
        Width = width;
        Height = height;
        Clusters = clusters;
        _a = a;
        _b = b;
        _t = t;
    }

    private readonly byte[] _a, _b, _t;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Largest share first.</summary>
    public IReadOnlyList<ColourCluster> Clusters { get; }

    /// <summary>The cluster covering most of pixel (x, y).</summary>
    public int Label(int x, int y)
    {
        int i = y * Width + x;
        return _t[i] < 128 ? _a[i] : _b[i];
    }

    /// <summary>How much of each pixel is cluster <paramref name="cluster"/>, 0…1 — what its contours are traced on.</summary>
    public CoverageMap Coverage(int cluster)
    {
        var v = new float[_a.Length];
        for (int i = 0; i < v.Length; i++)
        {
            float t = _t[i] / 255f, c = 0;
            if (_a[i] == cluster) c += 1 - t;
            if (_b[i] == cluster) c += t;
            v[i] = c;
        }
        return new CoverageMap(Width, Height, v);
    }

    /// <summary>The cluster touching most of the picture's border.</summary>
    public int BackgroundIndex()
    {
        int best = 0;
        for (int k = 1; k < Clusters.Count; k++) if (Clusters[k].BorderShare > Clusters[best].BorderShare) best = k;
        return best;
    }
}

public static class ColourClusters
{
    /// <summary>Separates <paramref name="image"/> into at most <paramref name="maxK"/> colours.</summary>
    public static ColourClusterSet Find(RasterImage image, int maxK = 8, ColourClusterOptions? options = null)
    {
        options ??= new ColourClusterOptions();
        if (maxK < 1) throw new ArgumentOutOfRangeException(nameof(maxK));
        var samples = Sample(image, options);

        // k = 1 … maxK, each from the same seed; the elbow picks one.
        int kMax = Math.Min(maxK, samples.Count);
        var fits = new List<Centre[]>();
        var err = new List<double>();
        for (int k = 1; k <= kMax; k++)
        {
            var c = KMeans(samples, k, options.Seed);
            fits.Add(c);
            err.Add(Error(samples, c));
        }
        int chosen = Elbow(err, options.NoiseFloor2);
        var centres = RefineOnFlat(samples, fits[chosen - 1]);
        centres = Merge(centres, options.MergeDeltaE);
        return Assign(image, centres, options.MaxThreads);
    }

    // ── Sampling ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SampleSet
    {
        public required Lab[] Lab;
        public required int[] Rgb;
        public required double[] Weight;
        public required bool[] Edge;
        public int Count => Lab.Length;
    }

    private static SampleSet Sample(RasterImage img, ColourClusterOptions o)
    {
        long n = (long)img.Width * img.Height;
        long step = Math.Max(1, (n + o.MaxSamples - 1) / o.MaxSamples);
        // A step sharing a factor with the width samples the same columns on every row and misses a thin vertical
        // line entirely; a step coprime with it walks every column.
        while (step > 1 && Gcd(step, img.Width) != 1) step++;
        double edge2 = o.EdgeDeltaE * o.EdgeDeltaE;
        var counts = new Dictionary<long, int>();
        var labCache = new Dictionary<int, Lab>();
        Lab LabOf(int rgb)
        {
            if (!labCache.TryGetValue(rgb, out var l)) labCache[rgb] = l = CieLab.FromRgb(rgb);
            return l;
        }
        for (long i = 0; i < n; i += step)
        {
            int x = (int)(i % img.Width), y = (int)(i / img.Width);
            int rgb = img.Rgb(x, y);
            var p = LabOf(rgb);
            bool isEdge = false;
            if (x > 0 && p.DeltaE2(LabOf(img.Rgb(x - 1, y))) > edge2) isEdge = true;
            else if (x < img.Width - 1 && p.DeltaE2(LabOf(img.Rgb(x + 1, y))) > edge2) isEdge = true;
            else if (y > 0 && p.DeltaE2(LabOf(img.Rgb(x, y - 1))) > edge2) isEdge = true;
            else if (y < img.Height - 1 && p.DeltaE2(LabOf(img.Rgb(x, y + 1))) > edge2) isEdge = true;
            long key = (uint)rgb | (isEdge ? 1L << 24 : 0L);
            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        var keys = counts.Keys.ToArray();
        Array.Sort(keys);
        var s = new SampleSet
        {
            Lab = new Lab[keys.Length], Rgb = new int[keys.Length], Weight = new double[keys.Length], Edge = new bool[keys.Length],
        };
        for (int i = 0; i < keys.Length; i++)
        {
            int rgb = (int)(keys[i] & 0xFFFFFF);
            s.Rgb[i] = rgb;
            s.Lab[i] = LabOf(rgb);
            s.Weight[i] = counts[keys[i]];
            s.Edge[i] = (keys[i] >> 24) != 0;
        }
        return s;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    // ── k-means++ ─────────────────────────────────────────────────────────────────────────────────────────────────

    private struct Centre
    {
        public Lab Lab;
        public double R, G, B;     // mean sRGB bytes of the members
        public double Weight;
    }

    /// <summary>SplitMix64 — a PRNG of our own, so the sequence does not change with the runtime's System.Random.</summary>
    private struct SplitMix(ulong seed)
    {
        private ulong _s = seed;

        public double Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }

    private static Centre[] KMeans(SampleSet s, int k, ulong seed)
    {
        var rng = new SplitMix(seed);
        var labs = new Lab[k];
        double total = s.Weight.Sum();
        labs[0] = s.Lab[Pick(s.Weight, total, rng.Next())];
        var d2 = new double[s.Count];
        for (int c = 1; c < k; c++)
        {
            double sum = 0;
            for (int i = 0; i < s.Count; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < c; j++) best = Math.Min(best, s.Lab[i].DeltaE2(labs[j]));
                d2[i] = best * s.Weight[i];
                sum += d2[i];
            }
            labs[c] = sum > 0 ? s.Lab[Pick(d2, sum, rng.Next())] : s.Lab[Math.Min(c, s.Count - 1)];
        }

        var assign = new int[s.Count];
        Array.Fill(assign, -1);
        var centres = new Centre[k];
        for (int iter = 0; iter < 100; iter++)
        {
            bool changed = false;
            for (int i = 0; i < s.Count; i++)
            {
                int a = Nearest(s.Lab[i], labs);
                if (a != assign[i]) { assign[i] = a; changed = true; }
            }
            centres = Means(s, assign, k, _ => true);
            for (int c = 0; c < k; c++)
            {
                if (centres[c].Weight > 0) { labs[c] = centres[c].Lab; continue; }
                // An empty cluster restarts on the sample farthest from every centre — deterministic, no draw.
                int far = 0;
                double fd = -1;
                for (int i = 0; i < s.Count; i++)
                {
                    double d = s.Lab[i].DeltaE2(labs[Nearest(s.Lab[i], labs)]);
                    if (d > fd) { fd = d; far = i; }
                }
                labs[c] = s.Lab[far];
                changed = true;
            }
            if (!changed) break;
        }
        return centres;
    }

    private static int Pick(double[] weights, double total, double u)
    {
        double target = u * total, acc = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            acc += weights[i];
            if (acc > target) return i;
        }
        return weights.Length - 1;
    }

    private static int Nearest(Lab p, Lab[] labs)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int j = 0; j < labs.Length; j++)
        {
            double d = p.DeltaE2(labs[j]);
            if (d < bd) { bd = d; best = j; }
        }
        return best;
    }

    private static Centre[] Means(SampleSet s, int[] assign, int k, Func<int, bool> take)
    {
        var sum = new double[k, 7];
        for (int i = 0; i < s.Count; i++)
        {
            if (!take(i)) continue;
            int a = assign[i];
            double w = s.Weight[i];
            sum[a, 0] += w * s.Lab[i].L;
            sum[a, 1] += w * s.Lab[i].A;
            sum[a, 2] += w * s.Lab[i].B;
            sum[a, 3] += w * ((s.Rgb[i] >> 16) & 0xFF);
            sum[a, 4] += w * ((s.Rgb[i] >> 8) & 0xFF);
            sum[a, 5] += w * (s.Rgb[i] & 0xFF);
            sum[a, 6] += w;
        }
        var c = new Centre[k];
        for (int j = 0; j < k; j++)
        {
            double w = sum[j, 6];
            if (w <= 0) continue;
            c[j] = new Centre
            {
                Lab = new Lab(sum[j, 0] / w, sum[j, 1] / w, sum[j, 2] / w),
                R = sum[j, 3] / w, G = sum[j, 4] / w, B = sum[j, 5] / w, Weight = w,
            };
        }
        return c;
    }

    // ── The error the elbow reads ─────────────────────────────────────────────────────────────────────────────────

    private static double Error(SampleSet s, Centre[] c)
    {
        double sum = 0, wsum = 0;
        for (int i = 0; i < s.Count; i++)
        {
            var p = s.Lab[i];
            double best = double.MaxValue;
            for (int j = 0; j < c.Length; j++) best = Math.Min(best, p.DeltaE2(c[j].Lab));
            if (s.Edge[i])
                for (int a = 0; a < c.Length; a++)
                    for (int b = a + 1; b < c.Length; b++)
                        best = Math.Min(best, SegmentDistance2(p, c[a].Lab, c[b].Lab));
            sum += s.Weight[i] * best;
            wsum += s.Weight[i];
        }
        return sum / wsum;
    }

    private static double SegmentDistance2(Lab p, Lab a, Lab b)
    {
        double dl = b.L - a.L, da = b.A - a.A, db = b.B - a.B;
        double len2 = dl * dl + da * da + db * db;
        double t = len2 <= 0 ? 0 : Math.Clamp(((p.L - a.L) * dl + (p.A - a.A) * da + (p.B - a.B) * db) / len2, 0, 1);
        return p.DeltaE2(new Lab(a.L + t * dl, a.A + t * da, a.B + t * db));
    }

    /// <summary>The smallest k whose error is down to the noise floor; failing that, the first k after which one more
    /// cluster removes less than 5 % of everything clustering removed.</summary>
    private static int Elbow(List<double> err, double floor)
    {
        for (int k = 1; k <= err.Count; k++) if (err[k - 1] <= floor) return k;
        double gain = err[0] - err[^1];
        if (gain <= 0) return 1;
        for (int k = 1; k < err.Count; k++) if (err[k - 1] - err[k] < 0.05 * gain) return k;
        return err.Count;
    }

    private static Centre[] RefineOnFlat(SampleSet s, Centre[] fit)
    {
        var labs = fit.Select(c => c.Lab).ToArray();
        var assign = new int[s.Count];
        for (int i = 0; i < s.Count; i++) assign[i] = Nearest(s.Lab[i], labs);
        var all = Means(s, assign, fit.Length, _ => true);
        var flat = Means(s, assign, fit.Length, i => !s.Edge[i]);
        var r = new Centre[fit.Length];
        for (int j = 0; j < fit.Length; j++)
        {
            r[j] = flat[j].Weight > 0 ? flat[j] : all[j];
            r[j].Weight = all[j].Weight;
        }
        return r.Where(c => c.Weight > 0).ToArray();
    }

    private static Centre[] Merge(Centre[] centres, double mergeDeltaE)
    {
        var list = centres.ToList();
        double lim2 = mergeDeltaE * mergeDeltaE;
        while (list.Count > 1)
        {
            int bi = -1, bj = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    double d = list[i].Lab.DeltaE2(list[j].Lab);
                    if (d < bd) { bd = d; bi = i; bj = j; }
                }
            if (bd >= lim2) break;
            var a = list[bi];
            var b = list[bj];
            double w = a.Weight + b.Weight, fa = a.Weight / w, fb = b.Weight / w;
            list[bi] = new Centre
            {
                Lab = new Lab(fa * a.Lab.L + fb * b.Lab.L, fa * a.Lab.A + fb * b.Lab.A, fa * a.Lab.B + fb * b.Lab.B),
                R = fa * a.R + fb * b.R, G = fa * a.G + fb * b.G, B = fa * a.B + fb * b.B, Weight = w,
            };
            list.RemoveAt(bj);
        }
        return [.. list];
    }

    // ── Every pixel as a two-colour mix ───────────────────────────────────────────────────────────────────────────

    private static ColourClusterSet Assign(RasterImage img, Centre[] centres, int maxThreads)
    {
        int k = centres.Length, w = img.Width, h = img.Height;
        var labs = centres.Select(c => c.Lab).ToArray();
        byte[] a = new byte[w * h], b = new byte[w * h], t = new byte[w * h];
        Bands.For(h, maxThreads, (y0, y1) =>
        {
            var memo = new Dictionary<int, int>();
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    int rgb = img.Rgb(x, y);
                    if (!memo.TryGetValue(rgb, out int m)) memo[rgb] = m = Mix(rgb, labs, centres);
                    int i = y * w + x;
                    a[i] = (byte)(m & 0xFF);
                    b[i] = (byte)((m >> 8) & 0xFF);
                    t[i] = (byte)(m >> 16);
                }
        });

        // Shares, counted in pixel order.
        var count = new long[k];
        var border = new long[k];
        long borderTotal = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int lab = t[i] < 128 ? a[i] : b[i];
                count[lab]++;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) { border[lab]++; borderTotal++; }
            }

        // Largest share first; the Lab breaks a tie so the order never depends on how the clustering got there.
        var order = Enumerable.Range(0, k)
            .OrderByDescending(j => count[j]).ThenBy(j => labs[j].L).ThenBy(j => labs[j].A).ThenBy(j => labs[j].B).ToArray();
        var remap = new byte[k];
        for (int r = 0; r < k; r++) remap[order[r]] = (byte)r;
        for (int i = 0; i < a.Length; i++) { a[i] = remap[a[i]]; b[i] = remap[b[i]]; }

        var clusters = new List<ColourCluster>(k);
        double n = (double)w * h;
        for (int r = 0; r < k; r++)
        {
            var c = centres[order[r]];
            int rgb = (Byte(c.R) << 16) | (Byte(c.G) << 8) | Byte(c.B);
            clusters.Add(new ColourCluster(r, c.Lab, rgb, count[order[r]] / n, borderTotal == 0 ? 0 : border[order[r]] / (double)borderTotal));
        }
        return new ColourClusterSet(w, h, clusters, a, b, t);
    }

    private static int Byte(double v) => Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>(a | b &lt;&lt; 8 | t &lt;&lt; 16): the nearest cluster, the cluster it mixes with, and how much of the
    /// second, in 1/255ths.</summary>
    private static int Mix(int rgb, Lab[] labs, Centre[] centres)
    {
        if (labs.Length == 1) return 0;
        var p = CieLab.FromRgb(rgb);
        int a = Nearest(p, labs), b = -1;
        double bd = double.MaxValue;
        for (int j = 0; j < labs.Length; j++)
        {
            if (j == a) continue;
            double d = SegmentDistance2(p, labs[a], labs[j]);
            if (d < bd) { bd = d; b = j; }
        }
        // The fraction is measured in the sRGB bytes the picture was blended in.
        double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, bl = rgb & 0xFF;
        double dr = centres[b].R - centres[a].R, dg = centres[b].G - centres[a].G, db = centres[b].B - centres[a].B;
        double len2 = dr * dr + dg * dg + db * db;
        double t = len2 <= 0 ? 0 : Math.Clamp(((r - centres[a].R) * dr + (g - centres[a].G) * dg + (bl - centres[a].B) * db) / len2, 0, 1);
        int tb = (int)Math.Round(t * 255);
        return a | (b << 8) | (tb << 16);
    }
}
