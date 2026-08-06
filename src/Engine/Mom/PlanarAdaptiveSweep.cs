// L9e / M1 — adaptive frequency sampling: the criterion, the interpolants, and the seeding.
//
// L9d measured 71.9 s per de-embedded point and ~73 minutes for a 101-point sweep, which is what
// makes this no longer optional. The saving is entirely in POINT COUNT: solve a subset, model the
// rest. Nothing here makes a single point cheaper.
//
// THE CRITERION IS ON S, NEVER ON A FIT RESIDUAL (D2), and this codebase has now found the reason
// twice by measurement. L7b-b's ModeCouplingResidual is *anti-correlated* with the terminal error in
// frequency and at 20 GHz exceeds it, so it is not even a bound; L8a's FitResidual picks the
// configuration whose far-field error is one of the worst. A criterion built on "how well does the
// interpolant fit the points it was built from" would be that mistake a third time — it is
// identically zero at every node by construction. What is measured instead is an ERROR: solve a
// frequency neither estimate was given, and compare the SOLVED S against what the interpolant
// PREDICTED there.
//
// This file is pure: no solve, no calibrator, no kernel. The refinement loop lives in
// PlanarSolve.Run because it owns the machinery; everything it needs to DECIDE lives here, so the
// decisions are testable without a 72-second solve.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>Which model of S(f) the unsolved points are produced from (D3).</summary>
public enum PlanarInterpolant
{
    /// <summary>
    /// <b>The free one</b> — <c>RFNetwork.Interpolate</c>'s complex cubic spline, already in the
    /// repository and already tested. D3's instruction is to try it first and let the measurement
    /// decide; see <c>AdaptiveSweepTests.T4_2</c> for what it decided.
    /// </summary>
    CubicSpline,

    /// <summary>
    /// <b>Floater-Hormann barycentric rational</b>, which is what §10.7 names when it says
    /// "rational". <b>It needs no pole extraction to EVALUATE</b> — the barycentric form is a ratio
    /// of two sums over the nodes — so it does not require the general complex eigensolver D9
    /// declines. Only INTERPRETING it as poles would, and nothing here does. Vector fitting, which
    /// genuinely does need one, is out.
    /// </summary>
    Rational,
}

/// <param name="Tolerance">
/// The stopping threshold, in absolute |ΔS|. Refinement stops in an interval once the solved
/// midpoint agrees with the interpolant's prediction there to better than this. <b>It is an error,
/// not a residual</b> — see this file's header.
/// </param>
/// <param name="Interpolant">D3's measurement, exposed as a setting rather than hard-wired.</param>
/// <param name="InitialPoints">
/// How many of the requested grid's points to solve before any refinement. Always includes both
/// endpoints; the interior seeds are evenly spaced by INDEX so the seed set is a deterministic
/// function of the requested grid alone.
/// </param>
/// <param name="MaxSolves">
/// A hard ceiling on solved points, so a pathological structure degrades to "the whole grid" rather
/// than to an unbounded run. Defaults to the grid itself, at which point adaptive sampling has cost
/// nothing but the comparisons.
/// </param>
public sealed record PlanarAdaptiveSettings(
    double            Tolerance      = 1e-3,
    PlanarInterpolant Interpolant    = PlanarInterpolant.CubicSpline,
    int               InitialPoints  = 5,
    int               MaxSolves      = int.MaxValue)
{
    public static readonly PlanarAdaptiveSettings Default = new();
}

/// <summary>
/// The pure half of M1: which points to solve first, how to model the rest, and how big the
/// disagreement is.
/// </summary>
public static class PlanarAdaptiveSweep
{
    /// <summary>
    /// <b>R-adf-3 — the seed set is a deterministic function of the requested grid.</b> Both
    /// endpoints, then interior indices evenly spaced by INDEX (never by frequency, which would make
    /// a log-spaced grid seed differently from a linear one carrying the same points). Two runs of
    /// the same problem start from the same set, which is the first half of "two runs are identical".
    /// </summary>
    public static int[] SeedIndices(int count, int initial)
    {
        if (count <= 0) return [];
        if (count <= 2 || initial >= count) return Enumerable.Range(0, count).ToArray();

        int want = Math.Max(2, initial);
        var set = new SortedSet<int> { 0, count - 1 };
        for (int i = 1; i < want - 1; i++)
            set.Add((int)Math.Round((double)i * (count - 1) / (want - 1)));
        return set.ToArray();
    }

    /// <summary>
    /// The largest absolute difference between two s-parameter matrices, entry by entry. <b>Absolute,
    /// not relative</b>: an s-parameter is already dimensionless and bounded, and a relative measure
    /// would make a deep notch (where |S| → 0 and the interpolant is doing its hardest work) demand
    /// unreachable precision while a flat pass-band demanded none.
    /// </summary>
    public static double WorstAbsDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double worst = 0;
        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
        return worst;
    }

    /// <summary>
    /// Model S at <paramref name="targets"/> from the solved <paramref name="nodes"/>.
    ///
    /// <para><b>A target that coincides with a node returns that node's own matrix, bit for bit</b>
    /// — not the interpolant's value there. Both interpolants pass through their nodes, but "passes
    /// through" is a mathematical statement about exact arithmetic and this is a promise about
    /// bytes: R-adf-2 says the published grid is the user's grid, and a user must be able to tell a
    /// solved point from a modelled one by the fact that the solved one is exactly what the solver
    /// produced.</para>
    /// </summary>
    public static Mat<Complex>[] Model(
        IReadOnlyList<double> nodes, IReadOnlyList<Mat<Complex>> values,
        IReadOnlyList<double> targets, PlanarInterpolant interpolant)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(targets);
        if (nodes.Count != values.Count)
            throw new ArgumentException("one value per node", nameof(values));
        if (nodes.Count == 0) throw new ArgumentException("at least one node", nameof(nodes));

        var outp = new Mat<Complex>[targets.Count];
        for (int t = 0; t < targets.Count; t++)
        {
            int exact = IndexOfNode(nodes, targets[t]);
            outp[t] = exact >= 0 ? values[exact] : PredictAt(nodes, values, targets[t], interpolant);
        }
        return outp;
    }

    /// <summary>The interpolant's own value at one frequency, node coincidence NOT short-circuited —
    /// this is what a refinement round compares a fresh solve against.</summary>
    public static Mat<Complex> PredictAt(
        IReadOnlyList<double> nodes, IReadOnlyList<Mat<Complex>> values, double at,
        PlanarInterpolant interpolant)
    {
        if (nodes.Count == 1) return values[0];

        int rows = values[0].RowCount, cols = values[0].ColCount;
        var result = new Mat<Complex>(rows, cols);

        // Both interpolants are per-entry over the complex plane, so the entry loop is outside.
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            var series = new Complex[nodes.Count];
            for (int k = 0; k < nodes.Count; k++) series[k] = values[k][i, j];
            result[i, j] = interpolant == PlanarInterpolant.Rational
                ? Barycentric(nodes, series, at)
                : Spline(nodes, series, at);
        }
        return result;
    }

    /// <summary>
    /// A node's index when <paramref name="f"/> IS one of the nodes. Exact <c>==</c> on purpose:
    /// every frequency here came from the same <c>double[]</c> the caller supplied, so a node and a
    /// target that mean the same point are the same bits. A tolerance would silently declare two
    /// genuinely distinct closely-spaced sweep points identical.
    /// </summary>
    private static int IndexOfNode(IReadOnlyList<double> nodes, double f)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i] == f) return i;
        return -1;
    }

    // ── Cubic spline over the complex plane, per component ────────────────────────────────────
    //
    // Natural (second derivative zero at both ends), solved by the standard tridiagonal sweep, on
    // the real and imaginary parts independently — which is exactly what RFNetwork.Interpolate's own
    // RealImag mode does. It is reproduced here rather than called because that API takes and
    // returns an SNP: building one per candidate frequency inside a refinement loop would allocate a
    // whole network to ask about one point, and the arithmetic is fifteen lines.

    private static Complex Spline(IReadOnlyList<double> x, Complex[] y, double at)
    {
        int n = x.Count;
        if (n == 2) return Linear(x, y, at);

        return new Complex(Spline1(x, y, at, im: false), Spline1(x, y, at, im: true));
    }

    private static double Spline1(IReadOnlyList<double> x, Complex[] y, double at, bool im)
    {
        int n = x.Count;
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = im ? y[i].Imaginary : y[i].Real;

        var h = new double[n - 1];
        for (int i = 0; i < n - 1; i++) h[i] = x[i + 1] - x[i];

        // Natural spline: solve for the second derivatives.
        var alpha = new double[n];
        for (int i = 1; i < n - 1; i++)
            alpha[i] = 3 * ((a[i + 1] - a[i]) / h[i] - (a[i] - a[i - 1]) / h[i - 1]);

        var l = new double[n];
        var mu = new double[n];
        var zz = new double[n];
        l[0] = 1;
        for (int i = 1; i < n - 1; i++)
        {
            l[i]  = 2 * (x[i + 1] - x[i - 1]) - h[i - 1] * mu[i - 1];
            mu[i] = h[i] / l[i];
            zz[i] = (alpha[i] - h[i - 1] * zz[i - 1]) / l[i];
        }
        l[n - 1] = 1;

        var c = new double[n];
        var b = new double[n];
        var d = new double[n];
        for (int i = n - 2; i >= 0; i--)
        {
            c[i] = zz[i] - mu[i] * c[i + 1];
            b[i] = (a[i + 1] - a[i]) / h[i] - h[i] * (c[i + 1] + 2 * c[i]) / 3;
            d[i] = (c[i + 1] - c[i]) / (3 * h[i]);
        }

        int seg = Segment(x, at);
        double dx = at - x[seg];
        return a[seg] + b[seg] * dx + c[seg] * dx * dx + d[seg] * dx * dx * dx;
    }

    private static Complex Linear(IReadOnlyList<double> x, Complex[] y, double at)
    {
        int s = Segment(x, at);
        double t = (at - x[s]) / (x[s + 1] - x[s]);
        return y[s] + t * (y[s + 1] - y[s]);
    }

    private static int Segment(IReadOnlyList<double> x, double at)
    {
        int n = x.Count;
        if (at <= x[0]) return 0;
        if (at >= x[n - 1]) return n - 2;
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (x[mid] <= at) lo = mid; else hi = mid;
        }
        return lo;
    }

    // ── Floater-Hormann barycentric rational ──────────────────────────────────────────────────
    //
    // Rational interpolation without solving for poles: the weights depend only on the NODE
    // POSITIONS, so the interpolant is a ratio of two sums over the data. Blending degree d = 3
    // (clamped to n-1) is the usual choice — it is pole-free on the real line for any node
    // distribution, which an ordinary Padé-style rational fit is not, and that guarantee is why
    // this shape is safe to evaluate blind inside a refinement loop.

    private static Complex Barycentric(IReadOnlyList<double> x, Complex[] y, double at)
    {
        int n = x.Count;
        int d = Math.Min(3, n - 1);

        var w = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = Math.Max(0, k - d); i <= Math.Min(k, n - 1 - d); i++)
            {
                double prod = 1;
                for (int j = i; j <= i + d; j++)
                    if (j != k) prod /= x[k] - x[j];
                sum += Math.Abs(prod);
            }
            w[k] = ((k & 1) == 0 ? 1 : -1) * sum;
        }

        Complex num = Complex.Zero;
        double den = 0;
        for (int k = 0; k < n; k++)
        {
            double dx = at - x[k];
            if (dx == 0) return y[k];
            double t = w[k] / dx;
            num += t * y[k];
            den += t;
        }
        return num / den;
    }
}
