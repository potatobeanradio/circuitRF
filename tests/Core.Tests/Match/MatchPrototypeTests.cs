using System.Diagnostics;
using CircuitRF.Core.Matching;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §6.2's numerical route: spectral factorisation plus Cauer extraction. Its agreement with
/// the Chebyshev closed form is what licenses the Butterworth and Bessel answers.
/// </summary>
public class MatchPrototypeTests(ITestOutputHelper output)
{
    private const double F1 = 3.3e9, F2 = 5.0e9;

    [Fact]
    public void Trim_DropsALeadingResidueButNotARealCoefficient()
    {
        // The cancellation in the spectral factorisation leaves ~1e-16 where it meant to leave 0. By
        // exact zero, a degree-3 polynomial then reports degree 4 and EVERY extraction returns null.
        Assert.Equal(4, MatchPoly.Trim([1e-16, 1.0, 2.0, 3.0, 4.0]).Length);
        Assert.Equal(5, MatchPoly.Trim([1e-3, 1.0, 2.0, 3.0, 4.0]).Length);
        Assert.Single(MatchPoly.Trim([0.0, 0.0, 0.0]));
    }

    [Fact]
    public void RootsOfARealPolynomial_AreFoundAndPolished()
    {
        var r = MatchPoly.Roots([1, -6, 11, -6]).Select(z => z.Real).Order().ToList();
        Assert.Equal(3, r.Count);
        Assert.Equal(1.0, r[0], 1e-10);
        Assert.Equal(2.0, r[1], 1e-10);
        Assert.Equal(3.0, r[2], 1e-10);

        // Degree 12 with complex roots — the worst case the Bessel denominator reaches.
        double[] p = MatchPoly.Mul([1, 0, 0, 0, 0, 0, 1], [1, 0, 0, 0, 0, 0, 1]);
        Assert.All(MatchPoly.Roots(p), z => Assert.True(MatchPoly.Eval(p, z).Magnitude < 1e-9));
    }

    [Fact]
    public void TheNumericalRoute_ReproducesTheChebyshevClosedForm()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        double target = d.Term2.QAt(d.Omega0) * d.W;
        double qFarRequired = d.Term1.QAt(d.Omega0);

        var search = MatchPrototypes.Search(ResponseShape.ChebyshevFano, 4, target, g =>
        {
            var (net, qFar) = MatchAbcdOracle.LadderFromG(d, g, 4);
            return new PrototypeEvaluation(qFar >= qFarRequired, qFar,
                                           MatchAbcdOracle.WorstS11Db(net, F1, F2, 201));
        });

        Assert.NotNull(search.G);
        double[] closed = MatchSynthesis.FanoG(4, d.Term2.QAt(d.Omega0), d.W)!;
        double worst = search.G!.Zip(closed, (a, b) => Math.Abs(a - b) / b).Max();
        output.WriteLine($"numerical vs closed form, worst relative error: {worst * 100:0.000} %");
        output.WriteLine("  numerical: " + string.Join(", ", search.G.Select(x => x.ToString("0.000000"))));
        output.WriteLine("  closed:    " + string.Join(", ", closed.Select(x => x.ToString("0.000000"))));
        Assert.True(worst < 5e-3, $"the numerical route is {worst * 100:0.00} % off the closed form");
        Assert.Equal(-16.663, search.Score, 0.02);
    }

    [Theory]
    [InlineData(2, -9.95)]
    [InlineData(4, -13.20)]
    [InlineData(6, -8.47)]
    public void Butterworth_IsAvailableAtEveryOrderAndMatchesTheDesignDoc(int n, double expectedDb)
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = n;
        d.Response = ResponseShape.Butterworth;

        var sw = Stopwatch.StartNew();
        var r = MatchSynthesis.Synthesize(d);
        sw.Stop();
        Assert.True(r.Ok, r.Refusal?.Message);

        double db = MatchAbcdOracle.WorstS11Db(r.Network!, F1, F2);
        output.WriteLine($"Butterworth n={n}: {db:0.000} dB, Q_far {r.QFarSynthesised:0.0000}, " +
                         $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(expectedDb, db, 0.25);
        Assert.True(r.QFarSynthesised >= r.QFarActual, "an infeasible member was returned");
    }

    [Fact]
    public void ButterworthAtOrderSix_RejectsItsOwnBestReturnLossMember()
    {
        // match.md §6.3: the far-end constraint tightens with order, which is the opposite of the
        // intuition that more elements always help. Without the constraint the search would return a
        // network that cannot absorb the far termination at all.
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 6;
        double target = d.Term2.QAt(d.Omega0) * d.W;
        double required = d.Term1.QAt(d.Omega0);

        double bestUnconstrainedDb = 0.0, itsQFar = 0.0;
        var search = MatchPrototypes.Search(ResponseShape.Butterworth, 6, target, g =>
        {
            var (net, qFar) = MatchAbcdOracle.LadderFromG(d, g, 6);
            double db = MatchAbcdOracle.WorstS11Db(net, F1, F2, 201);
            if (db < bestUnconstrainedDb) { bestUnconstrainedDb = db; itsQFar = qFar; }
            return new PrototypeEvaluation(qFar >= required, qFar, db);
        });

        output.WriteLine($"n=6 Butterworth: unconstrained best {bestUnconstrainedDb:0.000} dB at " +
                         $"Q_far {itsQFar:0.0000} (needs {required:0.0000}); " +
                         $"constrained best {search.Score:0.000} dB");

        Assert.True(bestUnconstrainedDb < search.Score,
                    "the constraint did not bind — this test is measuring nothing");
        Assert.True(itsQFar < required,
                    $"the best-RL member's Q_far {itsQFar:0.0000} is feasible after all");
        Assert.Equal(-8.47, search.Score, 0.25);
    }

    [Fact]
    public void Bessel_ClosesOnlyAtOrderTwoAndRefusesWithItsNumbers()
    {
        var results = new List<(int N, MatchSynthesisResult R, long Ms)>();
        foreach (int n in (int[])[2, 4, 6])
        {
            var d = MatchAbcdOracle.GoldenDesign();
            d.Order = n;
            d.Response = ResponseShape.Bessel;
            var sw = Stopwatch.StartNew();
            var r = MatchSynthesis.Synthesize(d);
            sw.Stop();
            results.Add((n, r, sw.ElapsedMilliseconds));
        }

        foreach (var (n, r, ms) in results)
            output.WriteLine(r.Ok
                ? $"Bessel n={n}: {MatchAbcdOracle.WorstS11Db(r.Network!, F1, F2):0.000} dB, " +
                  $"Q_far {r.QFarSynthesised:0.0000}, {ms} ms"
                : $"Bessel n={n}: refused, max Q_far {r.Refusal!.Numbers["maxQFar"]:0.0000} " +
                  $"against {r.Refusal.Numbers["qRequired"]:0.0000}, {ms} ms");

        var n2 = results[0].R;
        Assert.True(n2.Ok, n2.Refusal?.Message);
        Assert.Equal(-7.80, MatchAbcdOracle.WorstS11Db(n2.Network!, F1, F2), 0.25);

        foreach (var (n, r, _) in results.Skip(1))
        {
            Assert.False(r.Ok, $"Bessel n={n} was expected to be refused");
            Assert.Equal(MatchRefusalKind.ResponseInfeasible, r.Refusal!.Kind);
            Assert.Equal(1, r.Refusal.End);
            double max = r.Refusal.Numbers["maxQFar"];
            Assert.True(max < r.Refusal.Numbers["qRequired"], "the refusal's own numbers do not refuse");
            Assert.Contains(max.ToString("0.###"), r.Refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0.32, results[1].R.Refusal!.Numbers["maxQFar"], 0.05);
        Assert.Equal(0.18, results[2].R.Refusal!.Numbers["maxQFar"], 0.05);
    }

    [Fact]
    public void Extract_RefusesANonPositiveElement()
    {
        // A degree step that produces a negative g is the decisive realizability test, and it must
        // return null rather than a ladder nobody can build.
        Assert.Null(MatchPrototypes.Extract([-1.0, 0.0, 1.0], [1.0, 1.0], 2));
        Assert.Null(MatchPrototypes.Extract([1.0, 1.0], [1.0, 1.0], 2));
    }
}
