using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// R-ya4-8: the statistics functions, one case each, on a sample whose answers are worked by hand:
/// x = 2, 4, 4, 4, 5, 5, 7, 9 over a <c>trial</c> axis — mean 5, Σ(x − 5)² = 32 so σ = √(32/7), central moments
/// m₂ = 4, m₃ = 5.25, m₄ = 44.5. <c>xn</c> is the same with a NaN appended (a trial that did not evaluate);
/// <c>pass</c> marks the trials 1, 1, 0, 1, 0, 1, 1, 0.
/// </summary>
public sealed class StatisticsFunctionTests
{
    private static readonly double[] X = [2, 4, 4, 4, 5, 5, 7, 9];
    private static readonly double Sigma = Math.Sqrt(32.0 / 7);

    private static Evaluator Evaluator(out Scope scope)
    {
        static DataCube Trials(double[] v) => new([new Axis("trial", [.. Enumerable.Range(1, v.Length).Select(i => (double)i)])], v) { Unit = "V" };
        var ds = new DataSet();
        ds.Add("x", Trials(X));
        ds.Add("xn", Trials([.. X, double.NaN]));
        ds.Add("pass", Trials([1, 1, 0, 1, 0, 1, 1, 0]));
        // A distance swept around the trials: dist 10 has trials 1, 2, 3; dist 20 has 2, a refused trial, 4.
        ds.Add("g", new DataCube([new Axis("dist", [10.0, 20]), new Axis("trial", [1.0, 2, 3])], [1, 2, 3, 2, double.NaN, 4]) { Unit = "V" });
        scope = new Scope("t");
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["MC"] = ds }));
    }

    private static double Eval(string expr) => Evaluator(out var s).Eval(expr, s).AsReal();

    [Theory]
    [InlineData("mean_over(MC.x)",            5.0)]
    [InlineData("mean_over(MC.xn)",           5.0)]          // the NaN trial is skipped, not propagated
    [InlineData("median_over(MC.x)",          4.5)]
    [InlineData("pctl_over(MC.x, 25)",        4.0)]          // rank 1.75: 4 + 0.75·(4 − 4)
    [InlineData("skew_over(MC.x)",            0.65625)]      // 5.25 / 4^1.5
    [InlineData("kurt_over(MC.x)",           -0.21875)]      // 44.5 / 16 − 3
    [InlineData("yield_over(MC.xn > 4)",      0.5)]          // 5, 5, 7, 9 of the eight present
    [InlineData("max_over(MC.x)",             9.0)]          // the default axis is trial
    [InlineData("min_over(MC.x, \"trial\")",  2.0)]
    public void EachReduction_GivesTheWorkedValue(string expr, double expected)
        => Assert.Equal(expected, Eval(expr), 12);

    [Fact]
    public void TheSpreadMeasures_UseTheSampleSigma()
    {
        Assert.Equal(Sigma, Eval("std_over(MC.x)"), 12);
        Assert.Equal(5 / (3 * Sigma), Eval("cpk(MC.x, 0, 11)"), 12);          // the nearer limit is 0
        Assert.Equal(6 / (3 * Sigma), Eval("cpk(MC.x, \"none\", 11)"), 12);   // one-sided
        Assert.Equal(6 / Sigma, Eval("sigma_to(MC.x, 11)"), 12);
    }

    [Fact]
    public void Histogram_CountsPerBin_WithItsWidthAsACompanion()
    {
        var ev = Evaluator(out var scope);
        var h = ev.Eval("histogram(MC.x, 4, 2, 10)", scope).AsCube();
        Assert.Equal("bin", h.Axes[0].Name);
        Assert.Equal([3.0, 5, 7, 9], h.Axes[0].Values);
        Assert.Equal([1.0, 5, 1, 1], h.RealValues);
        Assert.Equal([2.0, 2, 2, 2], ev.TakeCompanions()["width"].RealValues);
    }

    [Fact]
    public void Cdf_IsTheSortedValuesAgainstTheirCumulativeFraction()
    {
        var c = Evaluator(out var s).Eval("cdf(MC.xn)", s).AsCube();
        Assert.Equal([2.0, 4, 4, 4, 5, 5, 7, 9], c.Axes[0].Values);
        Assert.Equal([.. Enumerable.Range(1, 8).Select(i => i / 8.0)], c.RealValues);
    }

    /// <summary>Over two axes a CDF used to be an error. It is one curve per point of the other axes, along the last (the innermost
    /// sweep) unless named, every curve on one shared value axis so a plot draws them as a family.</summary>
    [Fact]
    public void Cdf_OverTwoAxes_IsOneCurvePerDistance_OnASharedValueAxis()
    {
        var c = Evaluator(out var s).Eval("cdf(MC.g)", s).AsCube();
        Assert.Equal(["dist", "value"], c.Axes.Select(a => a.Name));
        Assert.Equal([1.0, 2, 3, 4], c.Axes[1].Values);
        Assert.Equal([1 / 3.0, 2 / 3.0, 1, 1,   0, 0.5, 0.5, 1], c.RealValues, new ToleranceComparer(1e-12));

        var byDist = Evaluator(out var s2).Eval("cdf(MC.g, \"dist\")", s2).AsCube();
        Assert.Equal(["trial", "value"], byDist.Axes.Select(a => a.Name));
    }

    private sealed class ToleranceComparer(double tol) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= tol;
        public int GetHashCode(double d) => 0;
    }

    [Fact]
    public void YieldSensitivity_IsTheYieldPerBinOfTheParameter_WithItsCount()
    {
        // Two bins over [2, 9]: 2…5 hold six trials of which four pass; 7 and 9 hold two, one passing.
        var ev = Evaluator(out var scope);
        var y = ev.Eval("yield_sens(MC.pass, MC.x, 2)", scope).AsCube();
        Assert.Equal([4 / 6.0, 0.5], y.RealValues);
        Assert.Equal([6.0, 2], ev.TakeCompanions()["count"].RealValues);
    }

    [Fact]
    public void APercentileAtAnInfiniteExtreme_IsTheExtreme()
    {
        // brief-yield-15 R-ya15-8: on an order statistic there is nothing to interpolate; 0 · (−∞ − −∞) was NaN.
        double[] worst = [double.NegativeInfinity, 1, 2];
        Assert.Equal(double.NegativeInfinity, SampleStatistics.Percentile(worst, 0));
        Assert.Equal(2, SampleStatistics.Percentile(worst, 100));
    }
}
