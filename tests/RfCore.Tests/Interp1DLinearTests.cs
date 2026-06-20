// ================================================================
//  Interp1DLinearTests.cs — correctness gate for Interp1DLinear
//
//  Matches scipy.interpolate.interp1d(x, y, kind='linear',
//  bounds_error=False): NaN for out-of-range queries.
// ================================================================

using System;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class Interp1DLinearTests
{
    private static void Near(double expected, double actual, double tol, string label = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tol,
            $"{label}: expected {expected}, got {actual}");
    }

    // ---- midpoint interpolation ------------------------------------
    [Fact]
    public void Midpoint_LinearData_Exact()
    {
        var interp = new Interp1DLinear(new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0, 2.0 });
        Near(0.5, interp.Eval(0.5), 1e-15, "midpoint 0-1");
        Near(1.5, interp.Eval(1.5), 1e-15, "midpoint 1-2");
    }

    [Fact]
    public void Midpoint_NonUniform_HandComputed()
    {
        // x=[0,2,6], y=[10,20,30]  → slope in [0,2] = 5/unit, in [2,6] = 2.5/unit
        var interp = new Interp1DLinear(new[] { 0.0, 2.0, 6.0 }, new[] { 10.0, 20.0, 30.0 });
        Near(15.0, interp.Eval(1.0), 1e-12, "midpoint interval 0");
        Near(25.0, interp.Eval(4.0), 1e-12, "midpoint interval 1");
    }

    // ---- node values reproduced exactly ----------------------------
    [Fact]
    public void NodeValues_ReturnedExactly()
    {
        double[] x = { 1.0, 3.0, 7.0, 10.0 };
        double[] y = { -5.0, 2.0, 8.0, 0.0 };
        var interp = new Interp1DLinear(x, y);
        for (int i = 0; i < x.Length; i++)
            Near(y[i], interp.Eval(x[i]), 1e-14, $"node[{i}]");
    }

    // ---- out-of-range returns NaN (scipy bounds_error=False) -------
    [Fact]
    public void OutOfRange_ReturnsNaN()
    {
        var interp = new Interp1DLinear(new[] { 1.0, 2.0, 3.0 }, new[] { 10.0, 20.0, 30.0 });
        Assert.True(double.IsNaN(interp.Eval(0.9)),  "below range → NaN");
        Assert.True(double.IsNaN(interp.Eval(3.001)), "above range → NaN");
        Assert.True(double.IsNaN(interp.Eval(-100.0)), "far below → NaN");
    }

    // ---- batch agrees with scalar ----------------------------------
    [Fact]
    public void BatchEval_MatchesScalar()
    {
        var interp = new Interp1DLinear(new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 3.0, 1.0 });
        double[] xs  = { -0.5, 0.25, 0.75, 1.5, 2.5 };
        double[] res = new double[xs.Length];
        interp.Eval(xs, res);
        for (int i = 0; i < xs.Length; i++)
        {
            double expected = interp.Eval(xs[i]);
            if (double.IsNaN(expected))
                Assert.True(double.IsNaN(res[i]), $"batch[{i}] should be NaN");
            else
                Near(expected, res[i], 1e-14, $"batch[{i}]");
        }
    }

    // ---- ascending check throws ------------------------------------
    [Fact]
    public void UnsortedX_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Interp1DLinear(new[] { 0.0, 2.0, 1.0 }, new[] { 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void DuplicateX_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new Interp1DLinear(new[] { 0.0, 1.0, 1.0 }, new[] { 1.0, 2.0, 3.0 }));
    }

    // ---- endpoints are in-range ------------------------------------
    [Fact]
    public void Endpoints_AreInRange()
    {
        var interp = new Interp1DLinear(new[] { 0.0, 5.0 }, new[] { 10.0, 20.0 });
        Near(10.0, interp.Eval(0.0), 1e-14, "lower endpoint");
        Near(20.0, interp.Eval(5.0), 1e-14, "upper endpoint");
    }
}
