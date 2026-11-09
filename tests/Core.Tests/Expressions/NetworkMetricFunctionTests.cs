using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-tuneopt-9 R-to9-1: each network-metric built-in equals <see cref="NetworkMetrics"/> on the
/// same S cube. The fixture is a 3-port with NON-uniform, partly complex reference impedances, so
/// every function has to find the run's Z0 and renormalize exactly as the Data Display does — a
/// function that assumed 50 Ω, or renormalized the whole network before extracting the pair, gives
/// different numbers here.
/// </summary>
public class NetworkMetricFunctionTests
{
    private static readonly Complex[] Z0 = [new(75, 0), new(50, 10), new(50, 0)];

    private static DataSet ThreePort()
    {
        var freq = new Axis("freq", [1e9, 1.5e9, 2e9, 2.5e9, 3e9], "Hz");
        var i    = new Axis("i", [1.0, 2.0, 3.0]);
        var j    = new Axis("j", [1.0, 2.0, 3.0]);
        var data = new Complex[5 * 9];
        for (int f = 0; f < 5; f++)
        {
            double t = f * 0.2;
            Complex P(double m, double deg) => Complex.FromPolarCoordinates(m, (deg - 40 * t) * Math.PI / 180);
            Complex[] s =
            [
                P(0.30 + 0.05 * t, -60), P(0.05, 30),          P(0.02, 10),
                P(3.0 - t, 100),         P(0.40, -40),         P(0.03, -20),
                P(0.02, 15),             P(0.03, 5),           P(0.10, 70),
            ];
            Array.Copy(s, 0, data, f * 9, 9);
        }
        var ds = new DataSet();
        ds.Add("S", new DataCube([freq, i, j], data));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(Z0));
        return ds;
    }

    private static double[] Eval(string expr, DataSet ds)
        => new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }))
               .Eval(expr, new Scope("t")).AsCube().RealValues;

    [Theory]
    [InlineData("mu",           NetworkMetric.Mu)]
    [InlineData("mu_prime",     NetworkMetric.MuPrime)]
    [InlineData("K",            NetworkMetric.K)]
    [InlineData("delta_mag",    NetworkMetric.DeltaMag)]
    [InlineData("max_gain",     NetworkMetric.MaxGain)]
    [InlineData("max_gain_lin", NetworkMetric.MaxGainLinear)]
    public void TwoPortMetric_EqualsNetworkMetrics(string fn, NetworkMetric metric)
    {
        var ds = ThreePort();
        var expected = NetworkMetrics.TwoPortMetric(ds, metric, 1, 2, out _);
        Assert.Equal(expected, Eval($"{fn}(SP1.S, 1, 2)", ds));
    }

    [Fact]
    public void Passivity_WholeNetworkAndPair_EqualNetworkMetrics()
    {
        var ds = ThreePort();
        Assert.Equal(NetworkMetrics.PassivityFull(ds, out _),       Eval("passivity(SP1.S)", ds));
        Assert.Equal(NetworkMetrics.PassivityPair(ds, 1, 2, out _), Eval("passivity(SP1.S, 1, 2)", ds));
    }

    [Fact]
    public void GroupDelayOfAPortPair_EqualsNetworkMetrics()
    {
        var ds = ThreePort();
        Assert.Equal(NetworkMetrics.GroupDelay(ds, 1, 2, out _), Eval("group_delay(SP1.S, 1, 2)", ds));
    }

    [Fact]
    public void AnNPortWithoutAPair_AndACubeNoAnalysisOwns_AreRefused()
    {
        var ds = ThreePort();
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));
        Assert.Contains("name the input and output port",
            Assert.ThrowsAny<ExpressionException>(() => ev.Eval("mu(SP1.S)", new Scope("t"))).Message);
        Assert.Contains("reference impedances are unknown",
            Assert.ThrowsAny<ExpressionException>(() => ev.Eval("K(SP1.S * 1)", new Scope("t"))).Message);
    }

    /// <summary>A two-port over two frequencies, with the noise cubes a Noise=yes run adds when <paramref name="noise"/>.</summary>
    private static DataSet TwoPort(bool noise)
    {
        var freq = new Axis("freq", [1e9, 2e9], "Hz");
        Complex[] s = [new(0.3, -0.2), new(0.05, 0), new(2, 1), new(0.4, 0.1)];
        var ds = new DataSet();
        ds.Add("S", new DataCube([freq, new Axis("i", [1.0, 2.0]), new Axis("j", [1.0, 2.0])], [.. s, .. s]));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube([new(50, 0), new(50, 0)]));
        if (noise)
        {
            ds.Add("NFmin", new DataCube([freq], new[] { 1.0, 1.2 }) { Unit = "dB" });
            ds.Add("Rn", new DataCube([freq], new[] { 10.0, 12.0 }) { Unit = "Ohm" });
            ds.Add("GammaOpt", new DataCube([freq], new Complex[] { new(0.3, 0.2), new(0.2, 0.3) }));
        }
        return ds;
    }

    private static string RefusalId(DataSet ds, string expr) =>
        Assert.ThrowsAny<ExpressionException>(() =>
            new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds })).Eval(expr, new Scope("t")))
              .Diagnostic!.Id;

    /// <summary>nf() and nf_circle() need a two-port run with Noise=yes, and say which of the two is missing.</summary>
    [Fact]
    public void NoiseFunctions_AreRefusedWithACode_OffATwoPortNoiseRun()
    {
        Assert.Equal("measure.noise.not-two-port", RefusalId(ThreePort(), "nf(SP1.S, 0)"));
        Assert.Equal("measure.noise.not-run", RefusalId(TwoPort(noise: false), "nf_circle(SP1.S, 2)"));
        Assert.Equal("measure.noise.source", RefusalId(TwoPort(noise: true), "nf(SP1.S, SP1.S)"));
        Assert.Equal("measure.noise.figure", RefusalId(TwoPort(noise: true), "nf_circle(SP1.S, j)"));
        Assert.Equal("measure.noise.circle-points", RefusalId(TwoPort(noise: true), "nf_circle(SP1.S, 2, 2)"));
    }

    /// <summary>nf_circle is {freq, point}, a closed loop, every point of it at the circle's NF by nf(); a circle below
    /// NFmin is NaN; nf at |Γs| ≥ 1 is NaN.</summary>
    [Fact]
    public void NfCircle_IsAClosedLoopPerFrequency_AtItsOwnNoiseFigure()
    {
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = TwoPort(noise: true) }));
        var c = ev.Eval("nf_circle(SP1.S, 2.5, 7)", new Scope("t")).AsCube();
        Assert.Equal(new[] { "freq", "point" }, c.Axes.Select(a => a.Name));
        var pts = c.ComplexValues;
        for (int f = 0; f < 2; f++)
        {
            Assert.True((pts[f * 7] - pts[f * 7 + 6]).Magnitude < 1e-12);
            for (int k = 0; k < 7; k++)
                Assert.All(ev.Eval($"nf(SP1.S, nf_circle(SP1.S, 2.5, 7)[:, {k}])", new Scope("t")).AsCube().RealValues,
                           v => Assert.Equal(2.5, v, 1e-12));
        }
        Assert.All(ev.Eval("nf_circle(SP1.S, 0.5)", new Scope("t")).AsCube().ComplexValues, z => Assert.True(double.IsNaN(z.Real)));
        Assert.All(ev.Eval("nf(SP1.S, 1)", new Scope("t")).AsCube().RealValues, v => Assert.True(double.IsNaN(v)));
    }
}
