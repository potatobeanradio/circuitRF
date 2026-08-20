using System.Numerics;

namespace CircuitRF.Core.Matching;

/// <summary>
/// Real polynomials as <b>descending</b> coefficient arrays, plus the root finder every closed form
/// and the numerical route share.
/// </summary>
public static class MatchPoly
{
    /// <summary>
    /// Drops leading coefficients that are zero <b>relative to the polynomial's own scale</b>.
    /// </summary>
    /// <remarks>
    /// <b>Never trim by exact zero.</b> The spectral-factorisation step is designed to cancel a
    /// leading coefficient; in floating point it leaves ~1e-16 instead of 0, a degree-3 polynomial
    /// then reports degree 4, the degree test in the Cauer extraction fails, and every extraction
    /// returns null with no diagnostic anywhere. The failure is clean, silent and total.
    /// </remarks>
    public static double[] Trim(double[] a, double tol = 1e-9)
    {
        if (a.Length == 0) return a;
        double m = a.Max(Math.Abs);
        if (m == 0.0) return [0.0];
        int i = 0;
        while (i < a.Length - 1 && Math.Abs(a[i]) < tol * m) i++;
        return a[i..];
    }

    /// <summary>Polynomial product.</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                r[i + j] += a[i] * b[j];
        return r;
    }

    /// <summary>Polynomial sum, right-aligned.</summary>
    public static double[] Add(double[] a, double[] b) => Combine(a, b, +1.0);

    /// <summary>Polynomial difference, right-aligned.</summary>
    public static double[] Sub(double[] a, double[] b) => Combine(a, b, -1.0);

    private static double[] Combine(double[] a, double[] b, double sign)
    {
        int n = Math.Max(a.Length, b.Length);
        var r = new double[n];
        for (int i = 0; i < a.Length; i++) r[n - a.Length + i] += a[i];
        for (int i = 0; i < b.Length; i++) r[n - b.Length + i] += sign * b[i];
        return r;
    }

    /// <summary>Evaluates at a complex point (Horner).</summary>
    public static Complex Eval(double[] a, Complex x)
    {
        Complex v = Complex.Zero;
        foreach (double c in a) v = v * x + c;
        return v;
    }

    /// <summary>Builds a monic real polynomial from a conjugate-closed root set.</summary>
    /// <remarks>
    /// The product is accumulated in COMPLEX arithmetic and the real part taken once, at the end.
    /// Taking it factor by factor is not the same thing: only the product over a conjugate PAIR is
    /// real, so dropping the imaginary part after each linear factor silently produces a different
    /// polynomial, and every downstream extraction then fails with no diagnostic.
    /// </remarks>
    public static double[] FromRoots(IEnumerable<Complex> roots)
    {
        Complex[] p = [Complex.One];
        foreach (var r in roots)
        {
            var q = new Complex[p.Length + 1];
            for (int i = 0; i < p.Length; i++)
            {
                q[i] += p[i];
                q[i + 1] -= p[i] * r;
            }
            p = q;
        }
        return [.. p.Select(z => z.Real)];
    }

    /// <summary>
    /// All roots of a real polynomial, by Durand-Kerner on a geometric-mean-scaled variable followed
    /// by a Newton polish on the original coefficients.
    /// </summary>
    /// <remarks>
    /// Degree here is at most 12 (a Bessel denominator at n = 6), which Durand-Kerner handles
    /// comfortably once the variable is scaled so every root is near the unit circle. The scaling is
    /// not cosmetic: unscaled, a Bessel denominator's coefficients span many orders of magnitude and
    /// the iteration stalls.
    ///
    /// <para><b>The scaling is the geometric mean of the root moduli, and the stopping test is
    /// relative.</b> Cauchy's bound (<c>1 + max|a_i/a_0|</c>) is an upper bound on the LARGEST root,
    /// so on a skewed polynomial it divides the whole set far below the unit circle instead of onto
    /// it — a Bessel denominator at alpha = 5 lands its roots near 1e-3, and the iteration then has
    /// to crawl inward from the unit-modulus seeds. Measured: 9-10 iterations for a Butterworth
    /// family against 112-200 for the Bessel family of the same degree, and at n = 6 the 200-iteration
    /// cap was reached with the roots still relatively wrong by a factor of 2.5. Both faults are one
    /// fault. <c>|a_n/a_0|^(1/n)</c> is the exact geometric mean of the moduli (the product of the
    /// roots is a_n/a_0), which puts the set ON the unit circle rather than under it, and an
    /// ABSOLUTE step test of 1e-14 is a different demand at every root modulus — it passes early on
    /// roots that are small and unreachable on roots that are large. Together they take the Bessel
    /// families to 9-16 iterations, 16-24x faster, at residuals equal to or better than before on
    /// every family and order this file synthesises.</para>
    /// </remarks>
    public static Complex[] Roots(double[] descending)
    {
        double[] a = Trim(descending, 1e-14);
        if (a.Length <= 1) return [];

        // Roots at the origin come out of trailing zeros exactly; peel them off first.
        int zeros = 0;
        while (a.Length - zeros > 1 && a[^ (zeros + 1)] == 0.0) zeros++;
        if (zeros > 0) a = a[..^zeros];
        if (a.Length <= 1) return [.. Enumerable.Repeat(Complex.Zero, zeros)];

        int n = a.Length - 1;

        // The scaling is the GEOMETRIC MEAN of the root moduli, |a_n/a_0|^(1/n), not Cauchy's
        // 1 + max|a_i/a_0| — see the remarks above for what the difference costs.
        double scale = Math.Pow(Math.Abs(a[^1] / a[0]), 1.0 / n);
        if (!double.IsFinite(scale) || scale <= 0.0) scale = 1.0;

        var c = new double[a.Length];
        double inv = 1.0 / a[0], p = 1.0;
        for (int i = 0; i <= n; i++) { c[i] = a[i] * inv * p; p /= scale; }

        var z = new Complex[n];
        var seed = new Complex(0.4, 0.9);
        Complex acc = Complex.One;
        for (int i = 0; i < n; i++) { acc *= seed; z[i] = acc; }

        for (int iter = 0; iter < 200; iter++)
        {
            // RELATIVE, for the same reason the scaling changed: an absolute floor is a different
            // demand at every root modulus, and it is the wrong one at both ends.
            double move = 0.0;
            for (int i = 0; i < n; i++)
            {
                Complex denom = Complex.One;
                for (int j = 0; j < n; j++) if (j != i) denom *= z[i] - z[j];
                if (denom == Complex.Zero) continue;
                Complex d = Eval(c, z[i]) / denom;
                z[i] -= d;
                double mag = z[i].Magnitude;
                move = Math.Max(move, d.Magnitude / (mag > 1e-300 ? mag : 1e-300));
            }
            if (move < 1e-13) break;
        }

        var result = new Complex[n + zeros];
        for (int i = 0; i < n; i++) result[i] = Polish(a, z[i] * scale);
        for (int i = 0; i < zeros; i++) result[n + i] = Complex.Zero;
        return result;
    }

    private static Complex Polish(double[] a, Complex x)
    {
        var d = new double[a.Length - 1];
        int n = a.Length - 1;
        for (int i = 0; i < n; i++) d[i] = a[i] * (n - i);
        for (int k = 0; k < 30; k++)
        {
            Complex f = Eval(a, x), fp = Eval(d, x);
            if (fp == Complex.Zero) break;
            Complex step = f / fp;
            x -= step;
            if (step.Magnitude < 1e-16 * Math.Max(1.0, x.Magnitude)) break;
        }
        return x;
    }
}

/// <summary>How one member of a response family scored once it was turned into a bandpass ladder.</summary>
/// <param name="Feasible">True when the far end can absorb its termination (Q_far &gt;= Q_actual).</param>
/// <param name="QFar">The far-end Q this member reaches.</param>
/// <param name="Score">Worst in-band |S11| in dB — lower is better.</param>
public sealed record PrototypeEvaluation(bool Feasible, double QFar, double Score);

/// <summary>What the one-parameter family search settled on.</summary>
/// <param name="G">The winning g-vector, or null when no member was feasible.</param>
/// <param name="MaxQFar">The largest Q_far the family reached, feasible or not — the refusal's number.</param>
/// <param name="ShapeParam">eps (Chebyshev/Butterworth) or alpha (Bessel) at the winner.</param>
/// <param name="OtherParam">K (Chebyshev/Butterworth) or C (Bessel) at the winner.</param>
/// <param name="Score">The winner's worst in-band |S11|, dB.</param>
public sealed record PrototypeSearchResult(
    double[]? G, double MaxQFar, double ShapeParam, double OtherParam, double Score);

/// <summary>
/// match.md §6.2's general route: write |Gamma|^2 as a two-parameter family in the lowpass-prototype
/// domain, spectrally factor it, and extract the ladder by continued fraction. Butterworth and Bessel
/// are synthesised this way, and running it on the Chebyshev family re-derives
/// <see cref="MatchSynthesis"/>'s closed form as a permanent cross-check.
/// </summary>
public static class MatchPrototypes
{
    /// <summary>
    /// The g-vector of one member, or null when that member is not realizable.
    /// </summary>
    /// <param name="shape">Chebyshev/Butterworth: the family is |Gamma|^2 = (K + e^2 F^2)/(1 + e^2 F^2).</param>
    /// <param name="n">Order, 2..6.</param>
    /// <param name="shapeParam">eps for Chebyshev/Butterworth, alpha for Bessel.</param>
    /// <param name="otherParam">K for Chebyshev/Butterworth (0 &lt; K &lt; 1), C for Bessel (0 &lt; C &lt;= 1).</param>
    public static double[]? Gvalues(ResponseShape shape, int n, double shapeParam, double otherParam)
        => Gvalues(shape, n, shapeParam, otherParam, null);

    /// <summary>
    /// A one-entry memo of the <b>denominator's</b> spectral factor, shared across the many
    /// <paramref name="otherParam"/> values one shape value is probed at.
    /// </summary>
    /// <remarks>
    /// <b>The denominator does not depend on the second parameter, in either family.</b> Chebyshev
    /// and Butterworth put K only in the numerator (<c>1 + e^2 F^2</c> against <c>K + e^2 F^2</c>);
    /// Bessel puts C only in the numerator (<c>theta*theta_-</c> against the same minus C). But
    /// <see cref="Search"/> holds the shape parameter fixed and sweeps the other one — 17 grid probes
    /// plus a bracketed solve, per shape value — so the same denominator was being spectrally
    /// factored dozens of times over. Since <see cref="Hurwitz"/> is a root-find and the two
    /// factorisations are the whole cost of <see cref="Gvalues"/>, hoisting it halves the search.
    ///
    /// <para>One entry is all that is wanted: the access pattern is a run of calls at one shape
    /// value, and a growing cache would only hold polynomials nothing will ask for again. The
    /// instance is created per <see cref="Search"/> call and never shared, which is also what keeps
    /// this safe to run on a background thread.</para>
    /// </remarks>
    private sealed class DenominatorMemo
    {
        private double _shapeParam = double.NaN;
        private int _n = -1;
        private ResponseShape _shape = (ResponseShape)(-1);
        private double[]? _monic;

        /// <summary>The denominator's monic left-half-plane factor, or null when it has none.</summary>
        public double[]? Get(ResponseShape shape, int n, double shapeParam, double[] den)
        {
            if (_n != n || _shape != shape || !_shapeParam.Equals(shapeParam))
            {
                double[] h = Hurwitz(den);
                _monic = h.Length == 0 ? null : h;
                _n = n;
                _shape = shape;
                _shapeParam = shapeParam;
            }
            return _monic;
        }
    }

    private static double[]? Gvalues(
        ResponseShape shape, int n, double shapeParam, double otherParam, DenominatorMemo? memo)
    {
        var (num, den) = Family(shape, n, shapeParam, otherParam);
        if (num is null || den is null) return null;

        double[]? dMonic = memo is null ? NullIfEmpty(Hurwitz(den)) : memo.Get(shape, n, shapeParam, den);
        if (dMonic is null) return null;

        double[] nn = Hurwitz(num);
        if (nn.Length == 0) return null;
        double[] d = Scale(dMonic, 1.0 / dMonic[0]);
        nn = Scale(nn, 1.0 / nn[0]);
        nn = Scale(nn, Math.Sqrt(Math.Abs(num[0] / den[0])));

        // Step 2: the sign of Gamma decides whether the first extracted element is shunt or series.
        // One sign cancels the leading coefficient of (d - nn) and the other of (d + nn), so exactly
        // one of the two orientations is available per sign; try both and take whichever extracts.
        foreach (double sign in (double[])[1.0, -1.0])
        {
            double[] a = MatchPoly.Trim(MatchPoly.Sub(d, Scale(nn, sign)));
            double[] b = MatchPoly.Trim(MatchPoly.Add(d, Scale(nn, sign)));
            double[] top, bot;
            if (a.Length == b.Length + 1) { top = a; bot = b; }
            else if (b.Length == a.Length + 1) { top = b; bot = a; }
            else continue;

            double[]? g = Extract(top, bot, n);
            if (g is not null) return g;
        }
        return null;
    }

    /// <summary>
    /// Cauer (continued-fraction) extraction of n reactive elements plus the terminating ratio.
    /// <b>A non-positive extracted element is the decisive realizability test</b>, not a warning.
    /// </summary>
    public static double[]? Extract(double[] num, double[] den, int n)
    {
        var g = new List<double>(n + 2) { 1.0 };
        double[] a = MatchPoly.Trim(num), b = MatchPoly.Trim(den);
        for (int k = 0; k < n; k++)
        {
            if (b.Length == 0 || a.Length != b.Length + 1) return null;
            double gk = a[0] / b[0];
            if (!double.IsFinite(gk) || gk <= 0.0) return null;
            double[] rem = MatchPoly.Trim(MatchPoly.Sub(a, MatchPoly.Mul([gk, 0.0], b)));
            (a, b) = (b, rem);
            g.Add(gk);
        }
        if (a.Length != 1 || b.Length != 1 || b[0] == 0.0) return null;
        double last = a[0] / b[0];
        if (!double.IsFinite(last) || last <= 0.0) return null;
        g.Add(last);
        return [.. g];
    }

    /// <summary>
    /// The constrained one-parameter search of match.md §6.2 step 5: pin g1 to Q*w, then choose the
    /// remaining freedom to maximise in-band return loss <b>subject to</b> the far end being
    /// absorbable.
    /// </summary>
    /// <remarks>
    /// <b>The feasibility constraint is not decoration.</b> At n = 6 on the design doc's problem the
    /// best-return-loss Butterworth member reaches Q_far = 0.51 against a required 0.638 and must be
    /// rejected; a worse-return-loss member reaches 0.647 and is accepted. An unconstrained search
    /// produces designs that cannot absorb the far termination.
    /// </remarks>
    public static PrototypeSearchResult Search(
        ResponseShape shape, int n, double g1Target, Func<double[], PrototypeEvaluation> evaluate)
    {
        (double lo, double hi) shapeRange = shape == ResponseShape.Bessel ? (0.05, 20.0) : (0.02, 3.0);
        (double lo, double hi) otherRange = shape == ResponseShape.Bessel ? (2e-3, 0.9995) : (1e-6, 0.999);

        var memo = new DenominatorMemo();
        double maxQFar = double.NegativeInfinity;
        double[]? bestG = null;
        double bestScore = double.PositiveInfinity, bestShape = 0, bestOther = 0;
        double maxQFarShape = 0;

        void Consider(double sp, double op, double[] g)
        {
            var ev = evaluate(g);
            if (ev.QFar > maxQFar) { maxQFar = ev.QFar; maxQFarShape = sp; }
            if (ev.Feasible && ev.Score < bestScore)
            {
                bestScore = ev.Score;
                bestG = g;
                bestShape = sp;
                bestOther = op;
            }
        }

        const int shapeSteps = 32;
        for (int i = 0; i <= shapeSteps; i++)
        {
            double sp = shapeRange.lo + (shapeRange.hi - shapeRange.lo) * i / shapeSteps;
            foreach (var (op, g) in SolveOther(shape, n, sp, g1Target, otherRange, memo))
                if (g.All(v => v > 0.0)) Consider(sp, op, g);
        }

        // Local refinement, around BOTH optima. The coarse grid finds the right neighbourhood but
        // the score is flat near its own optimum (without this the winning g-vector lands a few per
        // cent off the closed form), and the family's maximum Q_far is what an infeasible response
        // has to report in its refusal - a grid-limited number there would understate the family and
        // mislead the user about how far off it is.
        //
        // ROUND 1 IS THE REFUSAL'S NUMBER AND NOTHING ELSE. MaxQFar is read in exactly one place -
        // MatchSynthesis's ResponseInfeasible message - and that branch is only reached when no
        // member was feasible. So when one was, round 1 refines a quantity nobody will ever read, at
        // 44 of this method's 121 shape values: better than a third of the search, spent on a
        // sentence that is not going to be written. Skipping it changes no g-vector and no refusal.
        for (int round = 0; round < 2; round++)
        {
            double centre = round == 0 ? bestShape : maxQFarShape;
            if (round == 0 && bestG is null) continue;
            if (round == 1 && bestG is not null) continue;
            double step = (shapeRange.hi - shapeRange.lo) / shapeSteps;
            for (int pass = 0; pass < 4; pass++)
            {
                double lo = Math.Max(shapeRange.lo, centre - step);
                double hi = Math.Min(shapeRange.hi, centre + step);
                for (int i = 0; i <= 10; i++)
                {
                    double sp = lo + (hi - lo) * i / 10.0;
                    foreach (var (op, g) in SolveOther(shape, n, sp, g1Target, otherRange, memo))
                        if (g.All(v => v > 0.0)) Consider(sp, op, g);
                }
                centre = round == 0 ? bestShape : maxQFarShape;
                step = (hi - lo) / 10.0;
            }
        }

        return new PrototypeSearchResult(
            bestG, double.IsNegativeInfinity(maxQFar) ? 0.0 : maxQFar, bestShape, bestOther,
            bestG is null ? double.NaN : bestScore);
    }

    /// <summary>
    /// Solves the second free parameter so that g1 == the target, for one value of the shape
    /// parameter. Returns every bracket found, not just the first.
    /// </summary>
    /// <remarks>
    /// <b>The direction is detected, never assumed.</b> g1 rises with K for Chebyshev and Butterworth
    /// and falls with C for Bessel; a bisection written for one direction silently reports "no
    /// solution" for the other, which is how the design doc's first Bessel answer came out wrong.
    /// </remarks>
    private static IEnumerable<(double Op, double[] G)> SolveOther(
        ResponseShape shape, int n, double shapeParam, double target, (double lo, double hi) range,
        DenominatorMemo memo)
    {
        const int steps = 16;
        double? prevX = null, prevF = null;
        var found = new List<(double, double[])>();
        for (int i = 0; i <= steps; i++)
        {
            double x = range.lo + (range.hi - range.lo) * i / steps;
            double[]? g = Gvalues(shape, n, shapeParam, x, memo);
            double? f = g is null ? null : g[1] - target;
            if (f is { } fv && prevF is { } pf && prevX is { } px && pf * fv < 0.0)
            {
                var hit = Solve(shape, n, shapeParam, target, px, pf, x, fv,
                                (range.hi - range.lo) * 1e-12, memo);
                if (hit is { } h) found.Add(h);
            }
            if (f is not null) { prevX = x; prevF = f; }
        }
        return found;
    }

    /// <summary>
    /// Finds the bracketed root of <c>g1(op) - target</c> and returns the g-vector THERE, by the
    /// Illinois variant of regula falsi.
    /// </summary>
    /// <remarks>
    /// <b>The g-vector comes back with the root, and the iteration is not plain bisection.</b> Both
    /// halves of that are cost, and this is the hottest loop in the file: <see cref="Search"/> runs
    /// ~121 shape values, each scanning 17 points for brackets, so the refinement inside the brackets
    /// was ~70 % of every <see cref="Gvalues"/> call the search made. Plain bisection spends a fixed
    /// 40 evaluations to halve the interval 40 times; Illinois keeps bisection's guarantee (the root
    /// stays bracketed, so it cannot run away on a badly-shaped family the way a bare secant can)
    /// while converging superlinearly, and reaches the same interval in single figures. Returning the
    /// g-vector then removes the caller's own re-evaluation at the answer, which used to throw away
    /// the very last one this loop computed.
    /// </remarks>
    private static (double Op, double[] G)? Solve(
        ResponseShape shape, int n, double shapeParam, double target,
        double lo, double fLo, double hi, double fHi, double xTol, DenominatorMemo memo)
    {
        double tol = 1e-12 * Math.Max(1.0, Math.Abs(target));
        double[]? best = null;
        double bestX = 0.5 * (lo + hi);

        for (int k = 0; k < 60; k++)
        {
            double denom = fHi - fLo;
            double x = denom == 0.0 ? 0.5 * (lo + hi) : hi - fHi * (hi - lo) / denom;
            // A false-position step that leaves the bracket (or lands on an end) is replaced by the
            // bisection step, which is what keeps this bounded rather than merely usually fast.
            if (!double.IsFinite(x) || x <= Math.Min(lo, hi) || x >= Math.Max(lo, hi))
                x = 0.5 * (lo + hi);

            double[]? g = Gvalues(shape, n, shapeParam, x, memo);
            if (g is null) break;
            best = g;
            bestX = x;

            double fx = g[1] - target;
            if (Math.Abs(fx) <= tol || Math.Abs(hi - lo) <= xTol) break;

            if (fx * fHi < 0.0) { lo = hi; fLo = fHi; }
            else fLo *= 0.5;                       // the Illinois step: retire the stagnant end
            hi = x; fHi = fx;
        }

        return best is null ? null : (bestX, best);
    }

    /// <summary>
    /// (Num, Den) of |Gamma(jOmega)|^2 as real polynomials in s, via Omega = -j*s.
    /// </summary>
    private static (double[]? Num, double[]? Den) Family(
        ResponseShape shape, int n, double shapeParam, double otherParam)
    {
        if (n < 1) return (null, null);
        switch (shape)
        {
            case ResponseShape.Bessel:
            {
                double alpha = shapeParam, cc = otherParam;
                if (alpha <= 0 || cc <= 0) return (null, null);
                double[] theta = ReverseBessel(n);                     // descending in s, theta(0)=1
                int d = theta.Length - 1;
                var ths = new double[d + 1];
                var thm = new double[d + 1];
                for (int i = 0; i <= d; i++)
                {
                    ths[i] = theta[i] / Math.Pow(alpha, d - i);
                    thm[i] = ths[i] * ((d - i) % 2 == 0 ? 1.0 : -1.0);
                }
                double[] den = MatchPoly.Mul(ths, thm);
                double[] num = MatchPoly.Sub(den, [cc]);
                return (num, den);
            }
            default:
            {
                double kk = otherParam, eps = shapeParam;
                if (kk <= 0 || kk >= 1 || eps <= 0) return (null, null);
                double[] f2 = shape == ResponseShape.Butterworth
                    ? MonomialSquared(n)
                    : MatchPoly.Mul(ChebyshevT(n), ChebyshevT(n));
                double[] scaled = Scale(f2, eps * eps);
                double[] den = SubstituteOmega(MatchPoly.Add(scaled, [1.0]));
                double[] num = SubstituteOmega(MatchPoly.Add(scaled, [kk]));
                return (num, den);
            }
        }
    }

    /// <summary>Omega^(2m) -> (-1)^m s^(2m). Every family here is even in Omega, so the result is real.</summary>
    private static double[] SubstituteOmega(double[] inOmega)
    {
        int d = inOmega.Length - 1;
        var outS = new double[d + 1];
        for (int i = 0; i <= d; i++)
        {
            int k = d - i;                                  // power of Omega
            if (inOmega[i] == 0.0) continue;
            if (k % 2 != 0) throw new InvalidOperationException("odd power of Omega in an even family");
            outS[i] += inOmega[i] * ((k / 2) % 2 == 0 ? 1.0 : -1.0);
        }
        return outS;
    }

    private static double[] MonomialSquared(int n)
    {
        var f2 = new double[2 * n + 1];
        f2[0] = 1.0;
        return f2;
    }

    private static double[] ChebyshevT(int n)
    {
        double[] t0 = [1.0], t1 = [1.0, 0.0];
        if (n == 0) return t0;
        for (int k = 1; k < n; k++)
            (t0, t1) = (t1, MatchPoly.Sub(MatchPoly.Mul([2.0, 0.0], t1), t0));
        return t1;
    }

    /// <summary>theta_n(s), descending, normalised to theta_n(0) = 1.</summary>
    private static double[] ReverseBessel(int n)
    {
        var a = new double[n + 1];
        for (int k = 0; k <= n; k++)
            a[k] = Factorial(2 * n - k) / (Math.Pow(2, n - k) * Factorial(k) * Factorial(n - k));
        double a0 = a[0];
        var desc = new double[n + 1];
        for (int i = 0; i <= n; i++) desc[i] = a[n - i] / a0;
        return desc;
    }

    private static double Factorial(int k)
    {
        double v = 1.0;
        for (int i = 2; i <= k; i++) v *= i;
        return v;
    }

    private static double[] Scale(double[] a, double k) => [.. a.Select(x => x * k)];

    private static double[]? NullIfEmpty(double[] a) => a.Length == 0 ? null : a;

    /// <summary>The left-half-plane factor, monic.</summary>
    private static double[] Hurwitz(double[] poly)
    {
        var roots = MatchPoly.Roots(poly);
        if (roots.Length == 0) return [];
        var lhp = roots.Where(r => r.Real < 0.0).ToList();
        if (lhp.Count * 2 != roots.Length)
        {
            // Roots sitting on the imaginary axis (a perfectly matched member) make "strictly LHP"
            // ambiguous; take the lower half by real part so the factor is still degree n.
            lhp = [.. roots.OrderBy(r => r.Real).ThenBy(r => r.Imaginary).Take(roots.Length / 2)];
        }
        return MatchPoly.FromRoots(lhp);
    }
}
