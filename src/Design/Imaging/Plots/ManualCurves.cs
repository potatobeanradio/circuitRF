// A manual curve: found from one click, and read — brief-pdg-4-digitise-by-hand.md R-pdg4-2, R-pdg4-3.
//
// FIND CURVE IS THE EXISTING TRACER, STARTED FROM A CLICK. Follow runs PlotTraces.Find inside the box only (in box space,
// so a rotated box is followed on its resampled picture), with the click as TraceHints.Follow, and keeps the one curve that
// passes the click in the clicked ink's colour. Nothing else of the automatic reader runs: there is no frame to find and no
// word to read. What comes back is thinned to FIXPOINTS — one every spacingPx along x, plus every turning point — so the
// user edits a handful of points rather than hundreds of pixels. A click that follows nothing returns nothing and says
// why; it never invents a point.
//
// A MANUAL CURVE IS READ LIKE ANY TRACE. Its fixpoints are carried into box space and valued through the box's own
// calibrations; its samples are ManualSampling's grid, and its Curve.Points the fixpoints joined (by the same method) at
// every pixel column between the first and the last — so the probe, the overlay, the fit, the CSV and the Touchstone take
// it with no path of their own.

using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>What Find curve followed: fixpoints in picture pixels and the colour matched, or why nothing was.</summary>
public sealed record ManualFollowResult(IReadOnlyList<(double Px, double Py)> Fixpoints, int Rgb, string? Refusal)
{
    public bool Ok => Refusal is null;

    /// <summary>The other ink the click could have meant (EMB-5 R-emb5-10): a grey or black run — the plot's own grid
    /// or frame, as a rule — within reach of the click when a coloured one was followed instead. Null when there was none,
    /// or when a colour was asked for.</summary>
    public (int Rgb, int DistancePx)? Other { get; init; }
}

public static class ManualCurves
{
    /// <summary>The fixpoints' spacing along x a follow thins to, pixels.</summary>
    public const double DefaultSpacingPx = 20;

    /// <summary>How far from the click the followed curve may pass, pixels.</summary>
    public const double ReachPx = 6;

    /// <summary>
    /// Find curve (R-pdg4-2): the curve under <paramref name="seed"/> (picture pixels) inside <paramref name="plot"/>'s box,
    /// followed by the existing tracer and thinned to fixpoints every <paramref name="spacingPx"/> along x plus every turning
    /// point. <paramref name="rgb"/> is the colour to match; null takes the clicked ink's own. Pure.
    /// </summary>
    public static ManualFollowResult Follow(RasterImage picture, ManualPlot plot, (double X, double Y) seed, int? rgb = null,
                                            double spacingPx = DefaultSpacingPx)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return Follow(PlotInk.Of(picture), plot, seed, rgb, spacingPx);
    }

    /// <inheritdoc cref="Follow(RasterImage, ManualPlot, ValueTuple{double, double}, int?, double)"/>
    public static ManualFollowResult Follow(PlotInk ink, ManualPlot plot, (double X, double Y) seed, int? rgb = null,
                                            double spacingPx = DefaultSpacingPx)
    {
        ArgumentNullException.ThrowIfNull(ink);
        ArgumentNullException.ThrowIfNull(plot);
        var boxInk = plot.Axial ? ink : PlotInk.Of(plot.Resample(ink.Raster));
        var (sx, sy) = plot.ToBox(seed.X, seed.Y);
        var frame = plot.Frame(0);
        string at = $"({PlotReading.Fmt(Math.Round(seed.X, 1))}, {PlotReading.Fmt(Math.Round(seed.Y, 1))})";
        if (!frame.Inside(sx, sy))
            return new ManualFollowResult([], 0, $"the click at {at} px is outside the plot area");
        (int? picked, (int Rgb, int DistancePx)? other) = rgb is { } asked ? (asked, null) : InkAt(boxInk, sx, sy);
        if (picked is not { } colour)
            return new ManualFollowResult([], 0, $"the click at {at} px is on blank paper: no ink within 4 px of it");

        var found = PlotTraces.Find(boxInk, frame, [], [], new TraceHints([(sx, sy)], []));
        TraceCurve? best = null;
        double bestD = double.MaxValue;
        foreach (var c in found.Where(c => !c.Parametric && PlotInk.Distance(c.Rgb, colour) < 72))
        {
            double d = c.Points.Count == 0 ? double.MaxValue : c.Points.Min(p => Math.Sqrt((p.X - sx) * (p.X - sx) + (p.Y - sy) * (p.Y - sy)));
            if (d <= Math.Max(ReachPx, 2 * c.Width) && d < bestD) { bestD = d; best = c; }
        }
        if (best is null)
            return new ManualFollowResult([], colour, $"no curve of #{colour:x6} could be followed from the click at {at} px — place its points by hand")
                { Other = other };
        var thinned = Thin(best.Points, spacingPx);
        return new ManualFollowResult([.. thinned.Select(p => plot.FromBox(p.X, p.Y))], best.Rgb, null) { Other = other };
    }

    /// <summary>A pixel's chroma at or above this is coloured ink; below it is grey or black — a plot's grid and frame.</summary>
    internal const int ColouredChroma = 40;

    /// <summary>
    /// The colour a click on a curve meant, within 4 px of (x, y), or null on blank paper — and the grey or black ink it
    /// passed over when it chose a coloured one (R-emb5-10). A coloured run is preferred to grey or black ink whatever
    /// their contrast: a plot's own grid and frame are drawn grey or black, and a black 0 dB gridline two pixels under a
    /// coloured trace out-contrasts the trace. Within each class, nearer first, then the strongest. A curve drawn in
    /// black is still followed when no coloured ink is in reach.
    /// </summary>
    private static (int? Picked, (int Rgb, int DistancePx)? Other) InkAt(PlotInk ink, double x, double y)
    {
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        (int Rgb, int Score, int Reach)? coloured = null, grey = null;
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int xx = cx + dx, yy = cy + dy;
                if (!ink.At(xx, yy)) continue;
                int rgb = ink.Raster.Rgb(xx, yy);
                int reach = Math.Max(Math.Abs(dx), Math.Abs(dy));
                // Nearer first, then the strongest: the click's own stroke rather than a line two pixels off.
                int score = PlotInk.Distance(rgb, ink.Background) - 8 * reach;
                if (Chroma(rgb) >= ColouredChroma)
                {
                    if (coloured is null || score > coloured.Value.Score) coloured = (rgb, score, reach);
                }
                else if (grey is null || score > grey.Value.Score) grey = (rgb, score, reach);
            }
        if (coloured is { } c)
            return (c.Rgb, grey is { } g ? (g.Rgb, g.Reach) : null);
        return (grey?.Rgb, null);
    }

    private static int Chroma(int rgb)
    {
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }

    /// <summary>A followed curve's points (ascending x) thinned: the ends, one every <paramref name="spacing"/> along x,
    /// and every turning point — a local extremum standing at least a pixel proud of its neighbourhood.</summary>
    internal static List<(double X, double Y)> Thin(IReadOnlyList<(double X, double Y)> points, double spacing)
    {
        var pts = points.OrderBy(p => p.X).ToList();
        if (pts.Count <= 2) return pts;
        spacing = Math.Max(2, spacing);
        var keep = new SortedSet<int> { 0, pts.Count - 1 };
        // Turning points: the extreme of a window a quarter-spacing either side, a pixel above both of the window's ends.
        double reach = Math.Max(2, spacing / 4);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            int a = i, b = i;
            while (a > 0 && pts[i].X - pts[a - 1].X <= reach) a--;
            while (b < pts.Count - 1 && pts[b + 1].X - pts[i].X <= reach) b++;
            if (a == i || b == i) continue;
            double y = pts[i].Y;
            bool max = true, min = true;
            for (int k = a; k <= b; k++)
            {
                if (pts[k].Y > y || (pts[k].Y == y && k < i)) max = false;
                if (pts[k].Y < y || (pts[k].Y == y && k < i)) min = false;
            }
            if ((max && y - pts[a].Y >= 1 && y - pts[b].Y >= 1) || (min && pts[a].Y - y >= 1 && pts[b].Y - y >= 1)) keep.Add(i);
        }
        var turning = keep.ToList();
        // The regular ones, every spacing along x, not crowding a turning point.
        double next = pts[0].X + spacing;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            if (pts[i].X < next) continue;
            next = pts[i].X + spacing;
            if (turning.Any(t => Math.Abs(pts[t].X - pts[i].X) < spacing / 3)) continue;
            keep.Add(i);
        }
        return [.. keep.Select(i => pts[i])];
    }

    /// <summary>
    /// A manual curve as a trace of its plot (R-pdg4-2, R-pdg4-3): its fixpoints in box space valued through the plot's
    /// axes, sampled on the plot's grid, and joined at every pixel column between the first and the last. A curve that
    /// cannot be valued — its axis uncalibrated, or the right-hand scale asked of a plot with one — comes back with no
    /// samples and the sentence that says why.
    /// </summary>
    internal static (TraceCurve Curve, List<PlotSample> Samples, int Dropped, string? Refusal) Read(ManualCurve c, ManualPlot plot,
                                                                                                     RectCalibration rect, int frameIndex)
    {
        var box = c.Fixpoints.Select(p => plot.ToBox(p.Px, p.Py)).OrderBy(p => p.X).ToList();
        TraceCurve Curve(IReadOnlyList<(double X, double Y)> points) =>
            new(frameIndex, c.Rgb, TraceStyle.Solid, false, points, 0, [], [], 1);
        if (rect.X is not { } xa || rect.AxisOn(c.Axis) is not { } ya)
        {
            string why = c.Axis == PlotSide.Right && plot.Y2 is null
                ? "it is read on the right-hand scale, and the plot has one y scale"
                : "its plot's scales are not set";
            return (Curve(box), [], 0, why);
        }
        if (box.Count == 0) return (Curve(box), [], 0, null);

        bool xLog = xa.Scale == AxisScale.Log, yLog = ya.Scale == AxisScale.Log;
        var values = box.Select(p => (xa.ValueAt(p.X), ya.ValueAt(p.Y))).ToList();
        var sampled = ManualSampling.Sample(values, plot.Sampling, xLog, yLog);
        var samples = new List<PlotSample>(sampled.X.Count);
        for (int i = 0; i < sampled.X.Count; i++)
            samples.Add(new PlotSample(sampled.X[i], sampled.Y[i], default, xa.PixelAt(sampled.X[i]), ya.PixelAt(sampled.Y[i])));

        // The fixpoints joined by the curve's own method, one point a pixel column (the ends exactly where placed).
        var columns = new List<double> { box[0].X };
        for (double px = Math.Floor(box[0].X) + 1; px < box[^1].X; px++) columns.Add(px);
        if (box[^1].X > box[0].X) columns.Add(box[^1].X);
        var ys = ManualSampling.At(values, [.. columns.Select(xa.ValueAt)], plot.Sampling.Method, xLog, yLog);
        var joined = new List<(double X, double Y)>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
            if (double.IsFinite(ys[i])) joined.Add((columns[i], ya.PixelAt(ys[i])));
        return (Curve(joined), samples, sampled.Dropped, null);
    }
}
