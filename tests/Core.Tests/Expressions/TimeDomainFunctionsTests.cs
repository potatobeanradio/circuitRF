using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief TRN-5 gate 1 — the time-domain functions (docs/design/transient.md §8) on synthetic waveforms with closed-form
/// answers: a first-order step, a second-order step, a sine and a clipped sine.
/// </summary>
public sealed class TimeDomainFunctionsTests
{
    private static Value Eval(string expr, params (string Name, double[] T, double[] X)[] waves)
    {
        var ds = new DataSet();
        foreach (var (name, t, x) in waves) ds.Add(name, new DataCube([new Axis("time", t, "s")], x) { Unit = "V" });
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["TR1"] = ds })).Eval(expr, new Scope("t"));
    }

    private static (double[] T, double[] X) Sample(double stop, int n, Func<double, double> f)
    {
        var t = new double[n + 1];
        var x = new double[n + 1];
        for (int k = 0; k <= n; k++) { t[k] = stop * k / n; x[k] = f(t[k]); }
        return (t, x);
    }

    [Fact]
    public void FirstOrderStep_RiseIsTauLn9_AndSettleIsTauLn50()
    {
        const double tau = 1e-6;
        var (t, x) = Sample(20 * tau, 40_000, s => 1 - Math.Exp(-s / tau));
        var (_, down) = Sample(20 * tau, 40_000, s => Math.Exp(-s / tau));
        Assert.Equal(tau * Math.Log(9), Eval("rise(TR1.s)", ("s", t, x)).AsReal(), 1e-12);
        Assert.Equal(tau * Math.Log(9), Eval("fall(TR1.s)", ("s", t, down)).AsReal(), 1e-12);
        Assert.Equal(tau * Math.Log(50), Eval("settle(TR1.s, 2)", ("s", t, x)).AsReal(), 1e-12);
        Assert.True(double.IsNaN(Eval("fall(TR1.s)", ("s", t, x)).AsReal()), "a rising step has no fall");
    }

    [Fact]
    public void SecondOrderStep_OvershootAndUndershootFollowTheDampingRatio()
    {
        const double zeta = 0.3, wn = 2 * Math.PI * 1e6;
        double wd = wn * Math.Sqrt(1 - zeta * zeta);
        var (t, x) = Sample(20e-6, 200_000,
            s => 1 - Math.Exp(-zeta * wn * s) * (Math.Cos(wd * s) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(wd * s)));
        double decrement = zeta * Math.PI / Math.Sqrt(1 - zeta * zeta);
        Assert.Equal(100 * Math.Exp(-decrement), Eval("overshoot(TR1.s)", ("s", t, x)).AsReal(), 1e-5);
        Assert.Equal(100 * Math.Exp(-2 * decrement), Eval("undershoot(TR1.s)", ("s", t, x)).AsReal(), 1e-5);
    }

    [Fact]
    public void Sine_PeriodRmsAverageAndZeroThd()
    {
        const double f = 1e6, a = 2, dc = 0.5;
        var (t, x) = Sample(10 / f, 10_000, s => dc + a * Math.Sin(2 * Math.PI * f * s));
        var w = ("s", t, x);
        Assert.Equal(1 / f, Eval("period(TR1.s)", w).AsReal(), 1e-9 / f);
        Assert.Equal(f, Eval("frequency(TR1.s)", w).AsReal(), 1e-9 * f);
        Assert.Equal(Math.Sqrt(dc * dc + a * a / 2), Eval("rms(TR1.s)", w).AsReal(), 1e-12);
        Assert.Equal(dc, Eval("avg(TR1.s, 2u, 4u)", w).AsReal(), 1e-12);
        Assert.Equal(2.25e-6, Eval("tmax(TR1.s, 2u, 3u)", w).AsReal(), 1e-15);
        Assert.Equal(dc + a, Eval("value_at(TR1.s, 2.25u)", w).AsReal(), 1e-12);
        Assert.True(Math.Abs(Eval("thd(TR1.s, 1MHz)", w).AsReal()) <= 1e-9);
        // the spectrum, shaped as harmonic balance's: full amplitude, phase against t = 0
        var spec = Eval("fft(TR1.s, 1MHz)", w).AsCube();
        Assert.Equal("harmonic", Assert.Single(spec.Axes).Name);
        Assert.Equal("V", spec.Unit);
        Assert.Equal(dc, spec.ComplexValues[0].Real, 1e-12);
        Assert.True((spec.ComplexValues[1] - new Complex(0, -a)).Magnitude <= 1e-12);
    }

    [Fact]
    public void ClippedSine_ThdMatchesItsFourierSeries()
    {
        const double alpha = Math.PI / 4, c = 0.70710678118654752;   // clipped at sin(45°)
        var (t, x) = Sample(1e-6, 8192, s => Math.Clamp(Math.Sin(2 * Math.PI * 1e6 * s), -c, c));
        // b_n = (4/π)[∫₀^α sinθ sin nθ dθ + c ∫_α^{π/2} sin nθ dθ], odd n
        double B(int n) => 4 / Math.PI * (n == 1
            ? 0.5 * (alpha - Math.Sin(2 * alpha) / 2) + c * Math.Cos(alpha)
            : 0.5 * (Math.Sin((n - 1) * alpha) / (n - 1) - Math.Sin((n + 1) * alpha) / (n + 1)) + c * Math.Cos(n * alpha) / n);
        double expected = 100 * Math.Sqrt(Enumerable.Range(2, 9).Where(n => n % 2 == 1).Sum(n => B(n) * B(n))) / B(1);
        double thd = Eval("thd(TR1.s, 1MHz)", ("s", t, x)).AsReal();
        Assert.True(Math.Abs(thd - expected) <= 1e-6 * expected, $"thd {thd} against {expected}");
    }

    [Fact]
    public void EveryOtherAxisIsKept_AndTheSpectrumPutsItsHarmonicsLast()
    {
        // a transient result's V[time, node] inside a two-point sweep: [Vb, time, node]
        const int n = 200;
        var (t, _) = Sample(1e-6, n, s => s);
        var nodes = new Axis("node", [0, 1], "V", ["a", "out"]);
        var data = new double[2 * (n + 1) * 2];
        for (int b = 0; b < 2; b++)
            for (int k = 0; k <= n; k++)
            {
                data[(b * (n + 1) + k) * 2 + 0] = 0;
                data[(b * (n + 1) + k) * 2 + 1] = (b + 1) * Math.Cos(2 * Math.PI * 1e6 * t[k]);
            }
        var ds = new DataSet();
        ds.Add("V", new DataCube([new Axis("Vb", [1, 2]), new Axis("time", t, "s"), nodes], data));
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["TR1"] = ds }));
        var spec = ev.Eval("fft(TR1.V, 1MHz)", new Scope("t")).AsCube();
        Assert.Equal(["Vb", "node", "harmonic"], spec.Axes.Select(a => a.Name));
        var outOnly = ev.Eval("rms(TR1.V(\"out\"))", new Scope("t")).AsCube();
        Assert.Equal("Vb", Assert.Single(outOnly.Axes).Name);
        Assert.Equal(2 / Math.Sqrt(2), outOnly.RealValues[1], 1e-12);
    }

    [Fact]
    public void AValueWithNoTimeAxis_IsRefusedNamingTheFunction()
    {
        var ds = new DataSet();
        ds.Add("G", new DataCube([new Axis("freq", [1e9, 2e9], "Hz")], [1.0, 2.0]));
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));
        var ex = Assert.ThrowsAny<ExpressionException>(() => ev.Eval("overshoot(SP1.G)", new Scope("t")));
        Assert.Contains("overshoot()", ex.Message);
        Assert.Contains("no 'time' axis", ex.Message);
    }
}
