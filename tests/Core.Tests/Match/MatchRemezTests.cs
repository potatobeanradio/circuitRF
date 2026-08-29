using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §18.5 — the multi-interval Remez exchange, checked against the two cases that HAVE a
/// closed form and then against its own defining property on the cases that do not.
/// </summary>
/// <remarks>
/// <b>The two closed forms are the whole point of the first two gates.</b> A Remez exchange that
/// merely "looks equiripple" is worth nothing: the alternation theorem says the best polynomial on a
/// compact set is UNIQUE, so on a single interval it must be the shifted Chebyshev polynomial
/// <see cref="MatchFormPrototype"/> writes down by arccosine, and on two intervals of equal length in
/// u it must be <c>T_m(q(u))</c> for the quadratic q that maps both onto [-1, 1]. Reproducing them to
/// 1e-10 is a statement that the exchange found the answer and not merely an answer.
/// </remarks>
public class MatchRemezTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // ── Closed forms the exchange has to reproduce ────────────────────────────

    /// <summary>T_n(x(u)) with <c>x(u) = (2u - 1 - a^2)/(1 - a^2)</c>, descending in u.</summary>
    private static double[] ShiftedChebyshev(int n, double a)
        => Compose(n, [2.0 / (1.0 - a * a), -(1.0 + a * a) / (1.0 - a * a)]);

    /// <summary>T_n(q(u)) for an arbitrary inner polynomial q, by the Chebyshev recurrence.</summary>
    private static double[] Compose(int n, double[] q)
    {
        double[] prev = [1.0], cur = q;
        if (n == 0) return prev;
        for (int k = 2; k <= n; k++)
        {
            double[] next = MatchPoly.Sub(MatchPoly.Mul([2.0], MatchPoly.Mul(q, cur)), prev);
            prev = cur;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// The quadratic that maps two EQUAL-LENGTH intervals in u onto [-1, 1], two to one.
    /// </summary>
    /// <remarks>
    /// Equal length in u is exactly the condition that makes the two intervals symmetric about
    /// <c>v = (lo1 + hi2)/2</c>, so <c>y = (u - v)^2</c> carries both onto the SAME interval
    /// <c>[(v - hi1)^2, (v - lo1)^2]</c>, and one linear map takes that onto [-1, 1]. Hence
    /// <c>q(u) = (2(u - v)^2 - p - r)/(r - p)</c>, and <c>T_m(q(u))</c> is the equiripple polynomial
    /// of degree 2m on the union.
    /// </remarks>
    private static double[] QuadraticMap(double lo1, double hi1, double lo2, double hi2)
    {
        double v = 0.5 * (lo1 + hi2);
        double p = (v - hi1) * (v - hi1), r = (v - lo1) * (v - lo1);
        // (u - v)^2 = u^2 - 2vu + v^2
        double[] y = [1.0, -2.0 * v, v * v];
        double s = 2.0 / (r - p);
        return [y[0] * s, y[1] * s, y[2] * s - (p + r) / (r - p)];
    }

    private static double Rel(double[] got, double[] want)
    {
        double scale = want.Max(Math.Abs);
        double worst = 0.0;
        for (int i = 0; i < want.Length; i++)
            worst = Math.Max(worst, Math.Abs(got[i] - want[i]) / scale);
        return worst;
    }

    /// <summary>Aligns the sign of a polynomial to the reference's leading coefficient.</summary>
    private static double[] MatchSign(double[] got, double[] want)
        => Math.Sign(got[0]) == Math.Sign(want[0]) ? got : [.. got.Select(c => -c)];

    /// <summary>
    /// <b>Gate (a).</b> One interval <c>[a^2, 1]</c> reproduces the shifted Chebyshev polynomial of
    /// match.md §16.3 — the closed form the dual-band path uses — for every order and every bandwidth
    /// the multiband families reach.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.73)]
    public void OneInterval_ReproducesTheShiftedChebyshevPolynomial(double a)
    {
        double worst = 0.0;
        for (int n = 1; n <= MatchOrders.MaxOrder; n++)
        {
            var got = MatchRemez.Minimax(n, [(a * a, 1.0)]);
            Assert.NotNull(got);
            Assert.Equal(n + 1, got!.Length);

            double[] want = ShiftedChebyshev(n, a);
            double err = Rel(MatchSign(got, want), want);
            output.WriteLine($"a = {a}, n = {n}: relative coefficient error {err:e3}");
            worst = Math.Max(worst, err);
        }
        Assert.True(worst < 1e-10, $"worst relative coefficient error {worst:e3}");
    }

    /// <summary>
    /// <b>Gate (b).</b> Two intervals of EQUAL length in u reproduce the quadratic-mapping closed form
    /// <c>T_m(q(u))</c> — the one case on a union where the Akhiezer polynomial is writable.
    /// </summary>
    [Fact]
    public void TwoEqualLengthIntervals_ReproduceTheQuadraticMappingPolynomial()
    {
        (double Lo, double Hi)[] intervals = [(0.0, 0.25), (0.6, 0.85)];
        double[] q = QuadraticMap(0.0, 0.25, 0.6, 0.85);

        double worst = 0.0;
        for (int m = 1; m <= 3; m++)
        {
            var got = MatchRemez.Minimax(2 * m, intervals);
            Assert.NotNull(got);

            double[] want = Compose(m, q);
            double err = Rel(MatchSign(got!, want), want);
            output.WriteLine($"m = {m} (degree {2 * m}): relative coefficient error {err:e3}");
            worst = Math.Max(worst, err);
        }
        Assert.True(worst < 1e-10, $"worst relative coefficient error {worst:e3}");
    }

    // ── The defining property, on the sets that have no closed form ───────────

    /// <summary>Every local extremum of |p| on the union, with its sign.</summary>
    private static List<(double U, double P)> Extrema(
        double[] p, IReadOnlyList<(double Lo, double Hi)> intervals)
    {
        var found = new List<(double U, double P)>();
        foreach (var (lo, hi) in intervals)
        {
            const int N = 200001;
            double prev = 0, cur = 0;
            for (int i = 0; i < N; i++)
            {
                double u = lo + (hi - lo) * i / (N - 1.0);
                double v = MatchPoly.Eval(p, u).Real;
                if (i >= 2 && Math.Abs(cur) >= Math.Abs(prev) && Math.Abs(cur) >= Math.Abs(v))
                    found.Add((lo + (hi - lo) * (i - 1) / (N - 1.0), cur));
                if (i == 0 || i == N - 1) found.Add((u, v));
                prev = cur;
                cur = v;
            }
        }
        found.Sort((x, y) => x.U.CompareTo(y.U));

        // One point per same-sign run — the alternation set.
        var merged = new List<(double U, double P)>();
        foreach (var x in found)
        {
            if (merged.Count > 0 && Math.Sign(merged[^1].P) == Math.Sign(x.P))
            {
                if (Math.Abs(x.P) > Math.Abs(merged[^1].P)) merged[^1] = x;
                continue;
            }
            merged.Add(x);
        }
        return merged;
    }

    /// <summary>
    /// <b>Gate (c).</b> Exactly n + 1 alternating extrema, every one of magnitude 1, over the tri-band
    /// prototype's own sweep of interval pairs.
    /// </summary>
    /// <remarks>
    /// <b>This is the alternation theorem used as the oracle</b>, which is the only oracle available
    /// once the intervals stop being equal-length: a polynomial of degree n whose error equioscillates
    /// n + 1 times on E IS the minimax polynomial, uniquely. So checking the property checks the
    /// answer, and no reference implementation is needed.
    /// </remarks>
    [Fact]
    public void EveryCell_EquioscillatesAtExactlyDegreePlusOnePoints_AtMagnitudeOne()
    {
        (double A, double B)[] cells =
        [
            (0.10, 0.60), (0.25, 0.55), (0.40, 0.70), (0.05, 0.90), (0.50, 0.52), (0.30, 0.95),
        ];

        foreach (var (a, b) in cells)
        {
            (double Lo, double Hi)[] intervals = [(0.0, a * a), (b * b, 1.0)];
            for (int n = 1; n <= MatchOrders.MaxOrder; n++)
            {
                var p = MatchRemez.Minimax(n, intervals);
                Assert.NotNull(p);

                var ext = Extrema(p!, intervals);
                Assert.Equal(n + 1, ext.Count);
                foreach (var (_, v) in ext) Assert.Equal(1.0, Math.Abs(v), 1e-9);
            }
            output.WriteLine($"a = {a}, b = {b}: n = 1..6 all equioscillate");
        }
    }

    /// <summary>
    /// <b>Gate (d).</b> The weighted family reduces to the unweighted one as <c>uR -&gt; infinity</c>.
    /// </summary>
    /// <remarks>
    /// The weight is <c>sqrt(u + uR)</c>, which for large uR is the constant <c>sqrt(uR)</c> over the
    /// whole of a bounded E — so the two problems are the same problem up to that constant. Because
    /// <see cref="MatchRemez.MinimaxWeighted"/> normalises the WEIGHTED maximum to 1, the comparison
    /// is against <c>sqrt(uR) . R_k</c>, and its residual falling as 1/uR is the statement that
    /// nothing else differs.
    /// </remarks>
    [Fact]
    public void TheWeightedFamily_ReducesToTheUnweightedOne_AsTheExtraPoleRecedes()
    {
        (double Lo, double Hi)[] intervals = [(0.0, 0.16), (0.36, 1.0)];
        foreach (int k in (int[])[1, 2, 3])
        {
            double[] plain = MatchRemez.Minimax(k, intervals)!;
            double previous = double.PositiveInfinity;
            foreach (double uR in (double[])[1e3, 1e6, 1e9])
            {
                var r = MatchRemez.MinimaxWeighted(k, uR, intervals);
                Assert.NotNull(r);
                double[] scaled = [.. r!.Select(c => c * Math.Sqrt(uR))];
                double err = Rel(MatchSign(scaled, plain), plain);
                output.WriteLine($"k = {k}, uR = {uR:e0}: relative difference {err:e3}");
                Assert.True(err < previous, "the residual must fall as the pole recedes");
                previous = err;
            }
            Assert.True(previous < 1e-8, $"k = {k}: residual {previous:e3} at uR = 1e9");
        }
    }

    /// <summary>
    /// The weighted family's own defining property: <c>Phi = (u + uR) R_k^2</c> equioscillates between
    /// 0 and 1 at 2k + 2 points, which is what keeps
    /// <see cref="MatchFormPrototype.WorstInBand"/> true for the odd element counts.
    /// </summary>
    [Fact]
    public void TheWeightedFamily_PutsPhisInBandMaximumAtExactlyOne()
    {
        (double Lo, double Hi)[] intervals = [(0.04, 1.0)];
        foreach (int k in (int[])[1, 2, 3])
            foreach (double uR in (double[])[0.0, 0.05, 1.0, 25.0])
            {
                var r = MatchRemez.MinimaxWeighted(k, uR, intervals);
                Assert.NotNull(r);

                double worst = 0.0;
                for (int i = 0; i <= 20000; i++)
                {
                    double u = intervals[0].Lo + (intervals[0].Hi - intervals[0].Lo) * i / 20000.0;
                    double v = MatchPoly.Eval(r!, u).Real;
                    worst = Math.Max(worst, (u + uR) * v * v);
                }
                output.WriteLine($"k = {k}, uR = {uR}: max Phi = {worst:0.############}");
                Assert.Equal(1.0, worst, 1e-9);
            }
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A degenerate or out-of-range request is <b>null, never an exception</b> — the caller turns it
    /// into one of the synthesis's own refusals, and match.md §18.5 asks for a refusal upstream.
    /// </summary>
    [Fact]
    public void DegenerateInput_IsNull_NotAnException()
    {
        Assert.Null(MatchRemez.Minimax(0, [(0.0, 1.0)]));
        Assert.Null(MatchRemez.Minimax(MatchOrders.MaxOrder + 1, [(0.0, 1.0)]));
        Assert.Null(MatchRemez.Minimax(2, []));
        Assert.Null(MatchRemez.Minimax(2, [(1.0, 1.0)]));
        Assert.Null(MatchRemez.Minimax(2, [(0.0, 0.5), (0.4, 1.0)]));   // overlapping
        Assert.Null(MatchRemez.Minimax(2, [(0.0, double.NaN)]));
        Assert.Null(MatchRemez.MinimaxWeighted(2, -1.0, [(0.0, 1.0)]));
    }
}
