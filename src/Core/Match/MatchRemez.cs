namespace CircuitRF.Core.Matching;

/// <summary>
/// A prototype polynomial in <c>u</c>, <b>carried in the affinely scaled variable it was solved
/// in</b> — <c>t = (u - Beta)/Alpha</c>, with the passband hull on [-1, 1].
/// </summary>
/// <remarks>
/// <b>The scaling is not a convenience; dropping it costs five digits of the answer.</b> Measured
/// over <c>RESOLVED.md</c> §MN-LP's own 360-cell sweep: root-finding <c>p(u) -/+ j y</c> from
/// coefficients in u agrees with the closed form to 1e-9 up to order 4 and then decays to
/// <b>2.5e-4</b> at order 6 — not because the root finder is weak, but because a degree-6 polynomial
/// whose roots cluster in <c>[0.53, 1]</c> has coefficients spanning five decades, and Horner's rule
/// there cancels four of them before the Cauer extraction amplifies what is left. The SAME roots
/// found in t, where the coefficients are O(1) and the roots O(1), reproduce the closed form to
/// 5e-13 over the whole sweep.
///
/// <para>So a caller that means to root-find one of these must be handed the map, not a flattened
/// coefficient array. <see cref="InU"/> exists for the tests and for anyone who wants to READ the
/// polynomial; nothing in the synthesis path uses it.</para>
/// </remarks>
/// <param name="Scaled">Descending coefficients in t.</param>
/// <param name="Alpha">Half the hull's width in u. Positive.</param>
/// <param name="Beta">The hull's centre in u.</param>
public sealed record MatchPrototypePolynomial(double[] Scaled, double Alpha, double Beta)
{
    /// <summary>Degree in u, which is the degree in t.</summary>
    public int Degree => Scaled.Length - 1;

    /// <summary>The same polynomial's descending coefficients in <c>u</c>.</summary>
    public double[] InU
    {
        get
        {
            double[] inner = [1.0 / Alpha, -Beta / Alpha];
            double[] result = [Scaled[0]];
            for (int i = 1; i < Scaled.Length; i++)
                result = MatchPoly.Add(MatchPoly.Mul(result, inner), [Scaled[i]]);
            return result;
        }
    }

    /// <summary>Evaluates at a point in u — through t, for the reason the class remark gives.</summary>
    public double At(double u) => MatchPoly.Eval(Scaled, (u - Beta) / Alpha).Real;
}

/// <summary>
/// match.md §18.5: the equiripple polynomial on a <b>union of closed intervals</b> in
/// <c>u = Omega^2</c>, produced by a multi-interval Remez exchange.
/// </summary>
/// <remarks>
/// <b>This is not an optimiser in match.md §2.2's sense, and it must not become one.</b> The
/// alternation theorem holds on any compact set, so the best approximation on a union of intervals is
/// UNIQUE and the exchange computes it deterministically to machine precision. There is no spec to
/// meet, no tolerance a user can set, and no loop around it — the same inputs give the same
/// polynomial every time, exactly as an arccosine does for the single-interval case.
///
/// <para>The single-interval answer is the shifted Chebyshev polynomial <c>T_n(x(u))</c> that
/// <see cref="MatchFormPrototype"/> writes down in closed form (§16.3), and two equal-length intervals
/// have the closed form <c>T_m(q(u))</c> for a quadratic q. Neither closed form is used here: the
/// exchange reproduces both to 1e-12, and having ONE routine rather than three special cases is the
/// point. <c>MatchRemezTests</c> checks it against both.</para>
///
/// <h3>Why the linear problem, and not "make it equiripple"</h3>
/// <para>The monic minimax polynomial of degree n on a set E is <c>u^n - q(u)</c> where q is the best
/// degree-&lt;n approximation of <c>u^n</c> on E — a LINEAR approximation problem, which is what makes
/// the Remez exchange's inner step one small linear solve rather than a nonlinear iteration. The
/// weighted form <c>w(u)(u^k - q(u))</c> is the same linear problem with a weight, which is why
/// <see cref="MinimaxWeighted"/> is the same code with one function passed in.</para>
///
/// <h3>Scaled, from the start</h3>
/// <para>Everything is solved in <c>t</c>, the outer hull mapped onto [-1, 1], and the coefficients
/// are un-mapped once at the end. Here the raw variable is already <c>u in [0, 1]</c> and would be
/// harmless, but match.md §18.6's route B runs the same exchange with bands in Hz² — <c>u ~ 1e19</c>
/// — where a Vandermonde row of raw powers is not solvable at all. Writing it scaled costs four lines
/// and makes the class reusable there unchanged.</para>
/// </remarks>
public static class MatchRemez
{
    /// <summary>Grid points per interval. See <see cref="Polish"/> for why this need not be huge.</summary>
    /// <remarks>
    /// <b>Chebyshev-distributed within each interval, not uniform.</b> The extrema of the error crowd
    /// against the interval ENDS — that is what an equiripple polynomial does — and a uniform grid
    /// resolves the end extremum worst exactly where it matters most. A cosine distribution puts its
    /// densest sampling there.
    /// </remarks>
    private const int GridPerInterval = 2001;

    /// <summary>Exchange iterations before the exchange gives up and the caller refuses.</summary>
    private const int MaxIterations = 200;

    /// <summary>
    /// Relative agreement between the levelled error <c>h</c> and the grid maximum that counts as
    /// converged.
    /// </summary>
    private const double LevelTolerance = 1e-12;

    /// <summary>
    /// The monic-scaled minimax polynomial of degree <paramref name="n"/> on a union of closed
    /// intervals in u: <c>max|p| = 1</c> on the union, equioscillating at n + 1 points (§18.5).
    /// </summary>
    /// <param name="n">Degree, 1..<see cref="MatchOrders.MaxOrder"/>.</param>
    /// <param name="intervals">
    /// Disjoint closed intervals in ascending order, each of positive width. One interval is the
    /// ordinary single-band case and gives the shifted Chebyshev polynomial.
    /// </param>
    /// <returns>Descending coefficients in u, or null when the exchange did not converge.</returns>
    public static double[]? Minimax(int n, IReadOnlyList<(double Lo, double Hi)> intervals)
        => MinimaxScaled(n, intervals)?.InU;

    /// <inheritdoc cref="Minimax"/>
    /// <remarks>
    /// <b>This is the overload the synthesis uses</b>, and <see cref="Minimax"/> is the one a reader
    /// or a test uses — see <see cref="MatchPrototypePolynomial"/> for what flattening the map to
    /// coefficients in u costs a root finder.
    /// </remarks>
    /// <param name="n">Degree, 1..<see cref="MatchOrders.MaxOrder"/>.</param>
    /// <param name="intervals">As <see cref="Minimax"/>.</param>
    public static MatchPrototypePolynomial? MinimaxScaled(
        int n, IReadOnlyList<(double Lo, double Hi)> intervals)
        => Solve(n, null, intervals);

    /// <summary>
    /// The weighted form: the degree-<paramref name="k"/> polynomial minimising
    /// <c>max |sqrt(u + uR) . R_k(u)|</c> on the union, scaled so that weighted maximum is <b>1</b>
    /// (match.md §16.3, §18.5) — the family that carries the ODD element counts.
    /// </summary>
    /// <remarks>
    /// <b>The scaling is what makes <c>Phi = (u + uR) R_k(u)^2</c> have an in-band maximum of 1</b>,
    /// which is what lets <see cref="MatchFormPrototype.WorstInBand"/> keep meaning
    /// <c>(K + eps^2)/(1 + eps^2)</c> for this family too. Scaling <c>R_k</c> itself to 1 instead
    /// would put Phi's maximum at <c>u_max + uR</c> and quietly break every return-loss figure
    /// downstream.
    /// </remarks>
    /// <param name="k">Degree of R_k, 1..<see cref="MatchOrders.MaxOrder"/>.</param>
    /// <param name="uR">Position of the extra pole, <c>uR &gt;= 0</c> so that Phi &gt;= 0 on u &gt;= 0.</param>
    /// <param name="intervals">As <see cref="Minimax"/>.</param>
    public static double[]? MinimaxWeighted(
        int k, double uR, IReadOnlyList<(double Lo, double Hi)> intervals)
    {
        if (!(uR >= 0.0) || !double.IsFinite(uR)) return null;
        return Solve(k, uR, intervals)?.InU;
    }

    /// <inheritdoc cref="MinimaxWeighted"/>
    /// <param name="k">Degree of R_k, 1..<see cref="MatchOrders.MaxOrder"/>.</param>
    /// <param name="uR">Position of the extra pole, <c>uR &gt;= 0</c>.</param>
    /// <param name="intervals">As <see cref="Minimax"/>.</param>
    public static MatchPrototypePolynomial? MinimaxWeightedScaled(
        int k, double uR, IReadOnlyList<(double Lo, double Hi)> intervals)
    {
        if (!(uR >= 0.0) || !double.IsFinite(uR)) return null;
        return Solve(k, uR, intervals);
    }

    /// <summary>The exchange itself. <paramref name="uR"/> null is the unweighted problem.</summary>
    private static MatchPrototypePolynomial? Solve(
        int deg, double? uR, IReadOnlyList<(double Lo, double Hi)> intervals)
    {
        if (deg < 1 || deg > MatchOrders.MaxOrder) return null;
        if (intervals is null || intervals.Count == 0) return null;

        for (int i = 0; i < intervals.Count; i++)
        {
            var (lo, hi) = intervals[i];
            if (!double.IsFinite(lo) || !double.IsFinite(hi) || !(hi > lo)) return null;
            if (i > 0 && !(lo > intervals[i - 1].Hi)) return null;
        }
        if (deg + 1 < intervals.Count) return null;

        double uMin = intervals[0].Lo, uMax = intervals[^1].Hi;
        double alpha = 0.5 * (uMax - uMin), beta = 0.5 * (uMax + uMin);
        if (!(alpha > 0.0)) return null;

        var seg = new (double Lo, double Hi)[intervals.Count];
        for (int i = 0; i < intervals.Count; i++)
            seg[i] = ((intervals[i].Lo - beta) / alpha, (intervals[i].Hi - beta) / alpha);

        double r = uR ?? 0.0;
        bool weighted = uR is not null;
        double Weight(double t) => weighted ? Math.Sqrt(Math.Max(0.0, alpha * t + beta + r)) : 1.0;

        var grid = BuildGrid(seg);
        double[] reference = InitialReference(seg, deg + 1);
        double[]? q = null;
        double h = 0.0;
        bool converged = false;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            q = Level(reference, deg, Weight, out h);
            if (q is null) return null;

            var extrema = Extrema(grid, deg, q, Weight);
            if (extrema.Count < deg + 1) return null;

            double maxE = 0.0;
            foreach (var e in extrema) maxE = Math.Max(maxE, Math.Abs(e.E));
            if (!(maxE > 0.0)) return null;

            double level = Math.Abs(h);
            if (maxE - level <= LevelTolerance * maxE) { converged = true; break; }

            var next = Trim(extrema, deg + 1);
            if (next is null) return null;
            reference = next;
        }

        // An exchange that ran out of iterations is a REFUSAL upstream, not a nearly-right answer
        // quietly returned (match.md §18.5): the caller's own message names the band set, and a
        // polynomial whose extrema are not levelled is not the minimax polynomial of anything.
        if (!converged || q is null || !(Math.Abs(h) > 0.0) || !double.IsFinite(h)) return null;

        // p~(t) = (t^deg - q(t)) / |h|, descending in t. Dividing by |h| rather than h keeps the
        // leading coefficient positive; Phi is the SQUARE, so the sign is free either way.
        var c = new double[deg + 1];
        double inv = 1.0 / Math.Abs(h);
        c[0] = inv;
        for (int i = 1; i <= deg; i++) c[i] = -q[deg - i] * inv;

        return new MatchPrototypePolynomial(c, alpha, beta);
    }

    // ── The linear step ───────────────────────────────────────────────────────

    /// <summary>
    /// Solves <c>w_i (r_i^deg - q(r_i)) = (-1)^i h</c> for q's <paramref name="deg"/> coefficients and
    /// the levelled error h.
    /// </summary>
    /// <remarks>
    /// <b>Ascending coefficients here, unlike everywhere else in this library</b>, because the row is
    /// <c>[w, w r, w r^2, ...]</c> and writing it backwards to match <see cref="MatchPoly"/>'s
    /// descending convention would earn nothing. The one caller reverses it on the way out.
    /// </remarks>
    private static double[]? Level(double[] reference, int deg, Func<double, double> w, out double h)
    {
        h = 0.0;
        int m = deg + 1;
        var a = new double[m, m + 1];
        for (int i = 0; i < m; i++)
        {
            double t = reference[i], wi = w(t), p = wi;
            for (int k = 0; k < deg; k++) { a[i, k] = p; p *= t; }
            a[i, deg] = (i % 2 == 0) ? 1.0 : -1.0;
            a[i, m] = p;                       // w_i * t^deg, p having been multiplied deg times
        }

        var x = SolveLinear(a, m);
        if (x is null) return null;

        h = x[deg];
        return x[..deg];
    }

    /// <summary>Gaussian elimination with partial pivoting on a small dense augmented system.</summary>
    private static double[]? SolveLinear(double[,] a, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (!(Math.Abs(a[pivot, col]) > 0.0)) return null;

            if (pivot != col)
                for (int k = col; k <= n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);

            for (int row = col + 1; row < n; row++)
            {
                double f = a[row, col] / a[col, col];
                if (f == 0.0) continue;
                for (int k = col; k <= n; k++) a[row, k] -= f * a[col, k];
            }
        }

        var x = new double[n];
        for (int row = n - 1; row >= 0; row--)
        {
            double s = a[row, n];
            for (int k = row + 1; k < n; k++) s -= a[row, k] * x[k];
            x[row] = s / a[row, row];
            if (!double.IsFinite(x[row])) return null;
        }
        return x;
    }

    // ── The exchange step ─────────────────────────────────────────────────────

    /// <summary>One extremum of the error: where it is, and the SIGNED error there.</summary>
    private readonly record struct Extremum(double T, double E);

    /// <summary>
    /// Every local maximum of <c>|e|</c> on the union, in ascending order, each polished off the grid.
    /// </summary>
    /// <remarks>
    /// <b>Interval ENDPOINTS are always candidates.</b> On a union the error need not turn at an end —
    /// it is simply cut off there — so the largest |e| on a segment is frequently at its edge, and a
    /// pure interior-turning-point test would miss the extremum that carries the alternation.
    /// </remarks>
    private static List<Extremum> Extrema(
        double[][] grid, int deg, double[] q, Func<double, double> w)
    {
        var found = new List<Extremum>(2 * deg + 4);
        foreach (double[] t in grid)
        {
            int n = t.Length;
            var e = new double[n];
            for (int i = 0; i < n; i++) e[i] = Error(t[i], deg, q, w);

            for (int i = 0; i < n; i++)
            {
                bool isMax =
                    (i == 0 || Math.Abs(e[i]) >= Math.Abs(e[i - 1]))
                    && (i == n - 1 || Math.Abs(e[i]) >= Math.Abs(e[i + 1]));
                if (!isMax) continue;

                double lo = t[Math.Max(0, i - 1)], hi = t[Math.Min(n - 1, i + 1)];
                double best = i == 0 || i == n - 1 ? t[i] : Polish(lo, hi, deg, q, w);
                found.Add(new Extremum(best, Error(best, deg, q, w)));
            }
        }
        found.Sort((x, y) => x.T.CompareTo(y.T));

        // Merge same-sign runs to their largest — the multi-point exchange (§18.5). A run of like-sign
        // extrema contributes ONE alternation point, and taking the largest of the run is what makes
        // the exchange monotone in the levelled error.
        var merged = new List<Extremum>(found.Count);
        foreach (var x in found)
        {
            if (merged.Count > 0 && Math.Sign(merged[^1].E) == Math.Sign(x.E))
            {
                if (Math.Abs(x.E) > Math.Abs(merged[^1].E)) merged[^1] = x;
                continue;
            }
            if (x.E == 0.0) continue;
            merged.Add(x);
        }
        return merged;
    }

    /// <summary>Golden-section on |e| inside one grid cell pair — the extremum to machine precision.</summary>
    /// <remarks>
    /// <b>This is why the grid is 2,001 points and not 200,000.</b> The grid's job is to BRACKET each
    /// extremum, which a few thousand points do comfortably at degree 6; locating it is then 60 golden
    /// -section steps on a smooth unimodal function, and costs nothing. A grid dense enough to locate
    /// extrema by itself would have to be ~1e12 points to reach the same tolerance.
    /// </remarks>
    private static double Polish(double lo, double hi, int deg, double[] q, Func<double, double> w)
    {
        const double Phi = 0.6180339887498949;
        double x1 = hi - Phi * (hi - lo), x2 = lo + Phi * (hi - lo);
        double f1 = Math.Abs(Error(x1, deg, q, w)), f2 = Math.Abs(Error(x2, deg, q, w));
        for (int i = 0; i < 60 && hi - lo > 1e-16 * (1.0 + Math.Abs(hi)); i++)
        {
            if (f1 >= f2)
            {
                hi = x2; x2 = x1; f2 = f1;
                x1 = hi - Phi * (hi - lo);
                f1 = Math.Abs(Error(x1, deg, q, w));
            }
            else
            {
                lo = x1; x1 = x2; f1 = f2;
                x2 = lo + Phi * (hi - lo);
                f2 = Math.Abs(Error(x2, deg, q, w));
            }
        }
        return f1 >= f2 ? x1 : x2;
    }

    private static double Error(double t, int deg, double[] q, Func<double, double> w)
    {
        double v = 0.0, p = 1.0;
        for (int k = 0; k < deg; k++) { v += q[k] * p; p *= t; }
        return w(t) * (p - v);
    }

    /// <summary>
    /// Trims an alternating set to <paramref name="want"/> points by dropping the smaller-magnitude
    /// END, which is the one operation that cannot break alternation.
    /// </summary>
    private static double[]? Trim(List<Extremum> extrema, int want)
    {
        int first = 0, last = extrema.Count - 1;
        while (last - first + 1 > want)
        {
            if (Math.Abs(extrema[first].E) < Math.Abs(extrema[last].E)) first++;
            else last--;
        }
        if (last - first + 1 != want) return null;

        var next = new double[want];
        for (int i = 0; i < want; i++) next[i] = extrema[first + i].T;
        return next;
    }

    // ── Grid and initial reference ────────────────────────────────────────────

    private static double[][] BuildGrid((double Lo, double Hi)[] seg)
    {
        var grid = new double[seg.Length][];
        for (int i = 0; i < seg.Length; i++)
        {
            var t = new double[GridPerInterval];
            double mid = 0.5 * (seg[i].Lo + seg[i].Hi), half = 0.5 * (seg[i].Hi - seg[i].Lo);
            for (int j = 0; j < GridPerInterval; j++)
                t[GridPerInterval - 1 - j] =
                    mid + half * Math.Cos(Math.PI * j / (GridPerInterval - 1.0));
            t[0] = seg[i].Lo;
            t[^1] = seg[i].Hi;
            grid[i] = t;
        }
        return grid;
    }

    /// <summary>
    /// The starting reference: Chebyshev-like nodes on each interval, the count per interval in
    /// proportion to its length — and never fewer than one per interval.
    /// </summary>
    /// <remarks>
    /// <b>Proportional rather than equal, because the exchange has to start on the right side of the
    /// answer.</b> An equal split on <c>[0, 0.05] u [0.5, 1]</c> puts half the reference in a twentieth
    /// of the set, the first level solve then fits the tiny interval almost exactly, and the exchange
    /// spends its budget walking points back out. Proportional starts within one or two exchanges of
    /// the answer in every band set measured.
    /// </remarks>
    private static double[] InitialReference((double Lo, double Hi)[] seg, int want)
    {
        double total = 0.0;
        foreach (var s in seg) total += s.Hi - s.Lo;

        var counts = new int[seg.Length];
        int assigned = 0;
        for (int i = 0; i < seg.Length; i++)
        {
            counts[i] = Math.Max(1, (int)Math.Round((seg[i].Hi - seg[i].Lo) / total * want));
            assigned += counts[i];
        }
        // Reconcile the rounding against the longest interval, which can always give or take one.
        int longest = 0;
        for (int i = 1; i < seg.Length; i++)
            if (seg[i].Hi - seg[i].Lo > seg[longest].Hi - seg[longest].Lo) longest = i;
        while (assigned > want && counts[longest] > 1) { counts[longest]--; assigned--; }
        while (assigned > want)
        {
            for (int i = 0; i < seg.Length && assigned > want; i++)
                if (counts[i] > 1) { counts[i]--; assigned--; }
        }
        while (assigned < want) { counts[longest]++; assigned++; }

        var reference = new List<double>(want);
        for (int i = 0; i < seg.Length; i++)
        {
            double mid = 0.5 * (seg[i].Lo + seg[i].Hi), half = 0.5 * (seg[i].Hi - seg[i].Lo);
            int c = counts[i];
            for (int j = 0; j < c; j++)
                reference.Add(c == 1
                    ? mid
                    : mid - half * Math.Cos(Math.PI * j / (c - 1.0)));
        }
        reference.Sort();
        return [.. reference];
    }

}
