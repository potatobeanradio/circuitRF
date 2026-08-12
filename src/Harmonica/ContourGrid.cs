using System.Numerics;
using System.Threading;
using RfCore.Loadpull;

namespace CircuitRF.Harmonica;

/// <summary>Which quantity a contour map is of.</summary>
public enum GridMetric
{
    /// <summary>Output power at the compression target, dBm.</summary>
    PoutDbm,
    /// <summary>Drain efficiency at the compression target, per cent.</summary>
    DrainEfficiency,
    /// <summary>Power-added efficiency at the compression target, per cent.</summary>
    Pae,
}

/// <summary>One Γ point of the grid, after its Pin search.</summary>
/// <param name="Gamma">The termination, as Γ against the reference impedance.</param>
/// <param name="Z">The same termination as an impedance.</param>
/// <param name="Result">What the Pin search found. A hole when it did not compress.</param>
public sealed record GridPoint(Complex Gamma, Complex Z, PinSearchResult Result)
{
    /// <summary>R-hrf-8 — a point that did not reach compression before PinMax is a HOLE.</summary>
    public bool IsHole => !Result.Compressed;

    public double Metric(GridMetric metric)
    {
        var at = Result.AtCompression;
        if (at is null) return double.NaN;
        return metric switch
        {
            GridMetric.PoutDbm         => at.PoutW > 0 ? 10.0 * Math.Log10(at.PoutW) + 30.0 : double.NaN,
            GridMetric.DrainEfficiency => at.De  * 100.0,
            GridMetric.Pae             => at.Pae * 100.0,
            _ => double.NaN,
        };
    }
}

/// <summary>The best point on the grid for a metric (D6 — argmax, no search).</summary>
public sealed record GridExtremum(int Index, GridPoint Point, double Value);

/// <summary>
/// R-hrf-8 / R-hrf-9 / D5 / D6 — the Γ grid, its holes, the support mask, the extrema, and the RBF
/// factorization cache.
///
/// <para><b>Holes are thrown out, never extrapolated into (D5).</b> A Γ point that does not reach
/// the compression target before <c>PinMax</c> carries no value; its metric is NaN, which
/// <c>Rbf2D</c>'s own NaN-drop removes from the fit. That alone is not enough: an RBF over a scatter
/// with a hole punched in it rings, and will happily invent an efficiency ridge where there is no
/// data. So contours are additionally clipped to a SUPPORT MASK — the convex hull of the converged
/// points, minus a disc around each excluded one — and outside that mask nothing is drawn. This is a
/// correctness requirement rather than cosmetics: an invented ridge inside a hole is exactly the
/// artifact this tool must never produce.</para>
///
/// <para><b>MXP and MXE are the argmax over the COMPUTED grid (D6)</b> — no search, no
/// <c>PursuitEngine</c> call — so the summary readout is always consistent with what is drawn.</para>
///
/// <para><b>The factorization is cached on (positions, NaN mask).</b> Power and efficiency on one
/// grid share a factor, and so do successive frames of a drag during which the positions do not
/// move. The mask is part of the key because a point crossing in or out of a hole changes which
/// nodes exist — see <c>Rbf2D.Factored</c>.</para>
/// </summary>
public sealed class ContourGrid
{
    private readonly List<GridPoint> _points = [];
    private readonly Dictionary<GridMetric, Rbf2D> _fits = [];

    private Rbf2D.Factored? _factor;
    private bool[]          _factorMask = [];

    /// <summary>
    /// Reference impedance the Γ values are against. Re-read from <c>ctx.Model.Settings.Z0</c> at the
    /// START of every <see cref="Build"/> (R-h9b-6) — a worker's grid is a long-lived, reused object
    /// (§6.7), so its Z0 must track the document's own live value rather than freeze at construction.
    /// </summary>
    public double Z0 { get; private set; }

    public ContourGrid(double z0 = 50.0) => Z0 = z0;

    public IReadOnlyList<GridPoint> Points => _points;
    public int HoleCount => _points.Count(p => p.IsHole);
    public int ConvergedCount => _points.Count(p => !p.IsHole);

    /// <summary>How many times a kernel matrix has actually been factorized. The cache's own gate.</summary>
    public int FactorizationCount { get; private set; }

    /// <summary>Total HB solves the grid cost, tickles included.</summary>
    public int SolveCount => _points.Sum(p => p.Result.Solves);

    // ── building the grid ─────────────────────────────────────────────────────

    /// <summary>
    /// A ring grid: <paramref name="rings"/> × <paramref name="spokes"/> points inside the unit
    /// circle, plus the centre. The default coarse set of §6.8 is 3 × 12 = 37 points.
    /// </summary>
    public static Complex[] RingGrid(int rings, int spokes, double maxGamma = 0.8)
    {
        var g = new List<Complex> { Complex.Zero };
        for (int r = 1; r <= rings; r++)
        {
            double mag = maxGamma * r / rings;
            for (int s = 0; s < spokes; s++)
            {
                double ang = 2.0 * Math.PI * s / spokes;
                g.Add(Complex.FromPolarCoordinates(mag, ang));
            }
        }
        return [.. g];
    }

    /// <summary>
    /// Runs the whole grid. Each point warm-starts from its VSWR-NEAREST already-converged
    /// neighbour, which is the existing rule (<c>loadpull.md</c> §3.3) rather than a new one.
    /// </summary>
    /// <param name="tuneHarmonic">Which band the grid sweeps. 1 for the fundamental.</param>
    /// <param name="ct">
    /// R-h45-9's cancellation point. Checked BETWEEN Γ points, which is the natural granularity: one
    /// point is a whole <see cref="PinSearch"/> (a handful of HB solves), so a superseded frame stops
    /// within one point's cost rather than running the grid out. Finer granularity would mean
    /// threading a token through the HB Newton loop, which buys nothing at this frame rate.
    /// </param>
    public void Build(HarmonicaContext ctx, TerminationSet terminations,
                      IReadOnlyList<Complex> gammaGrid, TerminationSide side = TerminationSide.Load,
                      int tuneHarmonic = 1, CancellationToken ct = default,
                      bool reuseUnchanged = false)
    {
        // R-h9b-6 — this grid is a long-lived, per-worker object (§6.7); re-read Z0 from the document
        // on every build rather than freezing it at construction, or a Z0 change would silently keep
        // sweeping the OLD reference on every worker until the process restarted.
        Z0 = ctx.Model.Settings.Z0;

        // R-h7-12 — a DRAGGED grid point invalidates exactly one Γ sample. Everything else in the
        // scatter is at the identical Γ and was solved against the identical terminations, so its
        // PinSearch answer is bit-identical to what re-solving would produce. Keyed on the Γ value
        // itself rather than on an index, because an imported .gam may reorder the set.
        //
        // OFF by default: every other caller builds a grid the previous one has nothing to do with
        // (a new termination set, a new bias), and silently reusing there would be wrong.
        Dictionary<Complex, GridPoint>? previous = null;
        if (reuseUnchanged && _points.Count > 0 && _reusableAgainst == StateKey(ctx, terminations, side, tuneHarmonic))
        {
            previous = [];
            foreach (var p in _points) previous.TryAdd(p.Gamma, p);
        }

        _points.Clear();
        _fits.Clear();
        // The node SET moved, so Rbf2D.Factored's own cache key is stale even where the values are
        // not — R-h7-11 says so explicitly, and dropping it here is the one line that enforces it.
        _factor = null;
        ReusedPointCount = 0;

        var working = terminations.Clone();
        var converged = new List<(Complex Gamma, Complex[,] V, double? PinAtCompression)>();

        foreach (Complex gamma in gammaGrid)
        {
            ct.ThrowIfCancellationRequested();

            Complex z = Z0 * (Complex.One + gamma) / (Complex.One - gamma);

            if (previous is not null && previous.TryGetValue(gamma, out var kept))
            {
                _points.Add(kept);
                ReusedPointCount++;
                if (kept.Result.Steps.Count > 0)
                    converged.Add((gamma, kept.Result.Steps[^1].Point.V,
                                   kept.Result.AtCompression?.PavlDbm));
                continue;
            }

            working.Set(side, tuneHarmonic, z);

            var (seed, hint) = NearestByVswr(converged, gamma);
            var result = PinSearch.Run(ctx, working, seed, hint);

            _points.Add(new GridPoint(gamma, z, result));

            if (result.Steps.Count > 0)
                converged.Add((gamma, result.Steps[^1].Point.V, result.AtCompression?.PavlDbm));
        }

        _reusableAgainst = StateKey(ctx, terminations, side, tuneHarmonic);
    }

    /// <summary>
    /// How many points the last <see cref="Build"/> kept rather than re-solved. R-h7-12's own gate —
    /// a counter, not a clock.
    /// </summary>
    public int ReusedPointCount { get; private set; }

    /// <summary>
    /// What the held points were solved against. Reuse is only legitimate when every one of these is
    /// unchanged — the structure, the bias, the drive, the OTHER bands' terminations, and which band
    /// the grid sweeps. Anything else and a kept point answers a question nobody asked.
    /// </summary>
    private string? _reusableAgainst;

    private static string StateKey(HarmonicaContext ctx, TerminationSet t,
                                   TerminationSide side, int tuneHarmonic)
    {
        var sb = new System.Text.StringBuilder(ctx.Model.StructuralKey);
        sb.Append('|').Append(ctx.Model.Bias.Vgs).Append('|').Append(ctx.Model.Bias.Vds)
          .Append('|').Append(ctx.Model.Bias.Idq).Append('|').Append(ctx.Model.PavlDbm)
          .Append('|').Append(ctx.Model.Settings.CompressionDb)
          .Append('|').Append(ctx.Model.Settings.PinStartDbm)
          .Append('|').Append(ctx.Model.Settings.PinMaxDbm)
          .Append('|').Append(ctx.Model.Settings.Z0)
          .Append('|').Append(side).Append('|').Append(tuneHarmonic);

        for (int s = 0; s < 2; s++)
            foreach (int band in t.MarkedBands((TerminationSide)s).OrderBy(b => b))
            {
                // The band the grid SWEEPS is overwritten per point, so its marker value says nothing
                // about what a held point was solved at and must not be in the key.
                if ((TerminationSide)s == side && band == tuneHarmonic) continue;
                sb.Append('|').Append(s).Append(':').Append(band).Append('=')
                  .Append(t.Z((TerminationSide)s, band));
            }
        return sb.ToString();
    }

    /// <summary>
    /// The existing warm-start rule: seed from the converged neighbour closest in VSWR-like distance,
    /// which on the Γ plane is ordinary Euclidean distance.
    /// </summary>
    private static (Complex[,]? Seed, double? PinHint) NearestByVswr(
        List<(Complex Gamma, Complex[,] V, double? PinAtCompression)> converged, Complex gamma)
    {
        Complex[,]? best = null;
        double? hint = null;
        double bestD = double.MaxValue;
        foreach (var (g, v, pin) in converged)
        {
            double d = (g - gamma).Magnitude;
            if (d < bestD) { bestD = d; best = v; hint = pin; }
        }
        return (best, hint);
    }

    // ── D6 — the extrema ──────────────────────────────────────────────────────

    /// <summary>
    /// The argmax over the COMPUTED grid. No search and no interpolation, so the readout can never
    /// disagree with the contours drawn beside it.
    /// </summary>
    public GridExtremum? Extremum(GridMetric metric)
    {
        GridExtremum? best = null;
        for (int i = 0; i < _points.Count; i++)
        {
            double v = _points[i].Metric(metric);
            if (double.IsNaN(v)) continue;
            if (best is null || v > best.Value) best = new GridExtremum(i, _points[i], v);
        }
        return best;
    }

    /// <summary>MXP — the maximum-power grid point.</summary>
    public GridExtremum? Mxp => Extremum(GridMetric.PoutDbm);

    /// <summary>MXE — the maximum-efficiency grid point, drain efficiency by default (§7.2).</summary>
    public GridExtremum? Mxe => Extremum(GridMetric.DrainEfficiency);

    /// <summary>
    /// R-h9b-15 — the argmax of a Γ and its metric value.
    /// </summary>
    public readonly record struct InterpolatedExtremum(Complex Gamma, double Value);

    /// <summary>
    /// R-h9b-15 — the argmax of the FITTED surface, not of the samples: seeded from
    /// <paramref name="raster"/>'s own argmax cell (already computed once per panel per frame — this
    /// does not raster again), then refined by a local, resolution-INDEPENDENT high-resolution search
    /// on the same <see cref="Rbf2D"/> <see cref="Fit"/> the contours themselves are drawn from —
    /// so the glyph, the iso-lines and this answer can never describe different objects.
    ///
    /// <para>This is the same technique <c>LoadpullSurface.GetMxx</c> already uses for the identical
    /// problem (a local high-res grid search around the measured peak) — applied to a
    /// <see cref="ContourGrid"/> scatter instead of a loadpull <c>DataSet</c>, because the two data
    /// owners are not the same shape.</para>
    ///
    /// <para><b>Resolution-independent by construction, not by luck.</b> The refinement window's own
    /// resolution (<see cref="RefineSamples"/>) is FIXED regardless of the raster's — only the SEED
    /// (which cell to search near) comes from the raster, and a raster coarse enough to seed the wrong
    /// local basin entirely is the one failure mode this cannot correct; §D5's 96/256 pair is far
    /// finer than that in practice (state the measured agreement in the completion note).</para>
    ///
    /// <para><b>Respects the support mask.</b> Every candidate point is checked with
    /// <see cref="InSupport"/> against the SAME hull/hole-radius the raster used, so refinement can
    /// never wander into a region nothing converged.</para>
    /// </summary>
    public InterpolatedExtremum? InterpolatedArgmax(GridMetric metric, SurfaceGrid raster)
    {
        int nx = raster.XSpace.Length, ny = raster.YSpace.Length;
        if (nx == 0 || ny == 0) return null;

        int seedXi = -1, seedYi = -1;
        double seedVal = double.NegativeInfinity;
        for (int yi = 0; yi < ny; yi++)
            for (int xi = 0; xi < nx; xi++)
            {
                double v = raster.Values[yi * nx + xi];
                if (double.IsNaN(v) || v <= seedVal) continue;
                seedVal = v; seedXi = xi; seedYi = yi;
            }
        if (seedXi < 0) return null;   // every raster cell is a hole — "no optimum", not the origin

        var fit  = Fit(metric);
        var hull = ConvexHull([.. _points.Where(p => !p.IsHole).Select(p => p.Gamma)]);
        double holeRadius = HoleRadiusFactor * MeanNearestNeighbourSpacing();

        double stepRe = nx > 1 ? raster.XSpace[1] - raster.XSpace[0] : 0.1;
        double stepIm = ny > 1 ? raster.YSpace[1] - raster.YSpace[0] : 0.1;
        double cx = raster.XSpace[seedXi], cy = raster.YSpace[seedYi];
        double halfRe = Math.Max(Math.Abs(stepRe) * 2, 1e-6);
        double halfIm = Math.Max(Math.Abs(stepIm) * 2, 1e-6);

        var best = new Complex(cx, cy);
        double bestVal = seedVal;

        // A few zoom passes at a FIXED sample count each — the resolution independence claim.
        for (int pass = 0; pass < RefinePasses; pass++)
        {
            double foundRe = best.Real, foundIm = best.Imaginary;
            double localBest = double.NegativeInfinity;
            for (int yi = 0; yi < RefineSamples; yi++)
            {
                double im = best.Imaginary - halfIm + 2 * halfIm * yi / (RefineSamples - 1);
                for (int xi = 0; xi < RefineSamples; xi++)
                {
                    double re = best.Real - halfRe + 2 * halfRe * xi / (RefineSamples - 1);
                    if (!InSupport(re, im, hull, holeRadius)) continue;
                    double v = fit.Evaluate(re, im);
                    if (v > localBest) { localBest = v; foundRe = re; foundIm = im; }
                }
            }
            if (double.IsNegativeInfinity(localBest)) break;   // the window found nothing supported
            best = new Complex(foundRe, foundIm);
            bestVal = localBest;
            halfRe /= 4; halfIm /= 4;
        }

        return new InterpolatedExtremum(best, bestVal);
    }

    private const int RefineSamples = 25;
    private const int RefinePasses  = 3;

    // ── R-hrf-9 — the factorization cache ─────────────────────────────────────

    private double[] MetricValues(GridMetric metric)
    {
        var v = new double[_points.Count];
        for (int i = 0; i < _points.Count; i++) v[i] = _points[i].Metric(metric);
        return v;
    }

    private double[] NodesRe() => [.. _points.Select(p => p.Gamma.Real)];
    private double[] NodesIm() => [.. _points.Select(p => p.Gamma.Imaginary)];

    /// <summary>
    /// The fitted surface for a metric, re-using the cached kernel factorization whenever the node
    /// positions and the NaN mask are unchanged.
    /// </summary>
    public Rbf2D Fit(GridMetric metric)
    {
        if (_fits.TryGetValue(metric, out var cached)) return cached;

        double[] values = MetricValues(metric);
        double[] re = NodesRe(), im = NodesIm();

        if (_factor is null || !_factor.MatchesNaNMask(values))
        {
            _factor = Rbf2D.Factorize(re, im, values);
            _factorMask = [.. values.Select(v => !double.IsNaN(v))];
            FactorizationCount++;
        }

        var fit = _factor.Solve(values);
        _fits[metric] = fit;
        return fit;
    }

    /// <summary>Drops the fits but keeps the factorization — what a metric-values change costs.</summary>
    public void InvalidateValues() => _fits.Clear();

    // ── R-hrf-8 — the support mask, and the contours ──────────────────────────

    /// <summary>
    /// The radius of the disc excluded around each hole, as a fraction of the grid's mean nearest-
    /// neighbour spacing. One spacing: enough to cover the cell the missing point would have owned,
    /// and no more, so a single hole does not erase its neighbours' data.
    /// </summary>
    public double HoleRadiusFactor { get; init; } = 1.0;

    /// <summary>
    /// Whether a raster point is inside the support: within the convex hull of the converged points,
    /// and outside every hole's disc.
    /// </summary>
    public bool InSupport(double re, double im, IReadOnlyList<Complex> hull, double holeRadius)
    {
        if (!InsideHull(hull, re, im)) return false;

        foreach (var p in _points)
        {
            if (!p.IsHole) continue;
            double dr = re - p.Gamma.Real, di = im - p.Gamma.Imaginary;
            if (dr * dr + di * di < holeRadius * holeRadius) return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluates the fitted surface on a raster and blanks everything outside the support mask.
    /// <c>ContourExtractor</c> treats NaN cells as absent, so a masked cell contributes no polyline.
    /// </summary>
    public SurfaceGrid Raster(GridMetric metric, int resolution = 256)
    {
        var fit  = Fit(metric);
        var hull = ConvexHull([.. _points.Where(p => !p.IsHole).Select(p => p.Gamma)]);
        double holeRadius = HoleRadiusFactor * MeanNearestNeighbourSpacing();

        double minRe = -1, maxRe = 1, minIm = -1, maxIm = 1;
        if (_points.Count > 0)
        {
            minRe = _points.Min(p => p.Gamma.Real);
            maxRe = _points.Max(p => p.Gamma.Real);
            minIm = _points.Min(p => p.Gamma.Imaginary);
            maxIm = _points.Max(p => p.Gamma.Imaginary);
        }

        var xs = new double[resolution];
        var ys = new double[resolution];
        for (int i = 0; i < resolution; i++)
        {
            double t = resolution == 1 ? 0.5 : (double)i / (resolution - 1);
            xs[i] = minRe + t * (maxRe - minRe);
            ys[i] = minIm + t * (maxIm - minIm);
        }

        var values = new double[resolution * resolution];
        for (int yi = 0; yi < resolution; yi++)
            for (int xi = 0; xi < resolution; xi++)
                values[yi * resolution + xi] = InSupport(xs[xi], ys[yi], hull, holeRadius)
                    ? fit.Evaluate(xs[xi], ys[yi])
                    : double.NaN;

        return new SurfaceGrid(xs, ys, values);
    }

    /// <summary>Iso-lines for a metric, already clipped to the support mask.</summary>
    public IReadOnlyList<IsoPolyline> Contours(
        GridMetric metric, int levels = 10, int resolution = 256)
    {
        var grid = Raster(metric, resolution);
        var set  = ContourExtractor.LevelsBetween(grid, levels);
        return ContourExtractor.Extract(grid, set);
    }

    /// <summary>The disc radius a hole excludes, in Γ units — reported so a caller can draw it.</summary>
    public double HoleRadius => HoleRadiusFactor * MeanNearestNeighbourSpacing();

    private double MeanNearestNeighbourSpacing()
    {
        if (_points.Count < 2) return 0.1;
        double total = 0;
        int counted = 0;
        for (int i = 0; i < _points.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < _points.Count; j++)
            {
                if (i == j) continue;
                double d = (_points[i].Gamma - _points[j].Gamma).Magnitude;
                if (d < best) best = d;
            }
            if (best < double.MaxValue) { total += best; counted++; }
        }
        return counted > 0 ? total / counted : 0.1;
    }

    // ── convex hull (monotone chain) and point-in-polygon ─────────────────────

    internal static IReadOnlyList<Complex> ConvexHull(IReadOnlyList<Complex> pts)
    {
        if (pts.Count < 3) return pts;

        var sorted = pts.OrderBy(p => p.Real).ThenBy(p => p.Imaginary).ToList();

        static List<Complex> Chain(List<Complex> seq)
        {
            var half = new List<Complex>();
            foreach (var p in seq)
            {
                while (half.Count >= 2 && Cross(half[^2], half[^1], p) <= 0)
                    half.RemoveAt(half.Count - 1);
                half.Add(p);
            }
            half.RemoveAt(half.Count - 1);      // the last point starts the other half
            return half;
        }

        var lower = Chain(sorted);
        sorted.Reverse();
        var upper = Chain(sorted);

        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(Complex o, Complex a, Complex b)
        => (a.Real - o.Real) * (b.Imaginary - o.Imaginary)
         - (a.Imaginary - o.Imaginary) * (b.Real - o.Real);

    internal static bool InsideHull(IReadOnlyList<Complex> hull, double re, double im)
    {
        if (hull.Count < 3) return false;

        bool inside = false;
        for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
        {
            double xi = hull[i].Real, yi = hull[i].Imaginary;
            double xj = hull[j].Real, yj = hull[j].Imaginary;
            if (yi > im != yj > im &&
                re < (xj - xi) * (im - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }
}
