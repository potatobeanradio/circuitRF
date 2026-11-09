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

    /// <summary>A two-port over two frequencies, with the noise-wave correlation a Noise=yes run adds when
    /// <paramref name="noise"/>: Cs = kT0·[[1, 0.3 + 0.2j], [0.3 − 0.2j, 2.5]], so NF = 1.761 dB and NFmin = 1.641 dB.</summary>
    private static DataSet TwoPort(bool noise)
    {
        var freq = new Axis("freq", [1e9, 2e9], "Hz");
        var (i, j) = (new Axis("i", [1.0, 2.0]), new Axis("j", [1.0, 2.0]));
        Complex[] s = [new(0.3, -0.2), new(0.05, 0), new(2, 1), new(0.4, 0.1)];
        var ds = new DataSet();
        ds.Add("S", new DataCube([freq, i, j], [.. s, .. s]));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube([new(50, 0), new(50, 0)]));
        if (noise)
        {
            Complex[] cs = [.. new Complex[] { 1, new(0.3, 0.2), new(0.3, -0.2), 2.5 }.Select(c => c * NoiseMetrics.KT0)];
            ds.Add("Cs", new DataCube([freq, i, j], [.. cs, .. cs]) { Unit = "W/Hz" });
        }
        return ds;
    }

    private static string RefusalId(DataSet ds, string expr) =>
        Assert.ThrowsAny<ExpressionException>(() =>
            new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds })).Eval(expr, new Scope("t")))
              .Diagnostic!.Id;

    /// <summary>The noise functions need a run with Noise=yes; rn and ns_circle a two-port, nf and te an output port
    /// other than the source; ns_circle one of its two forms. Each refusal says which, with a code.</summary>
    [Theory]
    [InlineData("three", "rn(SP1.S)",                        "measure.noise.not-two-port")]
    [InlineData("three", "ns_circle(SP1.S, 2)",              "measure.noise.not-two-port")]
    [InlineData("three", "nf(SP1.S, 0, 3)",                  "measure.noise.not-run")]
    [InlineData("quiet", "ns_circle(SP1.S, 2)",              "measure.noise.not-run")]
    [InlineData("quiet", "te(SP1.S)",                        "measure.noise.not-run")]
    [InlineData("noisy", "nf(SP1.S, SP1.S)",                 "measure.noise.source")]
    [InlineData("noisy", "nf(SP1.S, 0, 3)",                  "measure.noise.port")]
    [InlineData("noisy", "te(SP1.S, 0, 1)",                  "measure.noise.port")]
    [InlineData("noisy", "te(SP1.S, 0, 2.5)",                "measure.noise.port")]
    [InlineData("noisy", "ns_circle(SP1.S, j)",              "measure.noise.figure")]
    [InlineData("noisy", "ns_circle(SP1.S, 2, 2)",           "measure.noise.circle-points")]
    [InlineData("noisy", "ns_circle(SP1.S)",                 "measure.noise.circle-form")]
    [InlineData("noisy", "ns_circle(2, 1.5)",                "measure.noise.circle-form")]
    [InlineData("noisy", "ns_circle(SP1.S, 2, 1.5, 0.2)",    "measure.noise.circle-form")]
    [InlineData("noisy", "ns_circle(2, j, 0.3, 0.2)",        "measure.noise.parameter")]
    [InlineData("noisy", "ns_circle(2, 1.5, 0.3, rn(SP1.S) * j)", "measure.noise.parameter")]
    [InlineData("noisy", "ns_circle(2, 1.5, SP1.S, rn(SP1.S))", "measure.noise.parameter")]
    public void NoiseFunctions_AreRefusedWithACode(string fixture, string expr, string id) =>
        Assert.Equal(id, RefusalId(fixture switch { "three" => ThreePort(), "quiet" => TwoPort(false), _ => TwoPort(true) }, expr));

    /// <summary>ns_circle is {freq, point}, a closed loop, every point of it at the circle's NF by nf(); a circle below
    /// NFmin is NaN; nf at |Γs| ≥ 1 is NaN; nf with no source is the noise factor from port 1's Z0, and te is T0·(F − 1).</summary>
    [Fact]
    public void NsCircle_IsAClosedLoopPerFrequency_AtItsOwnNoiseFigure()
    {
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = TwoPort(noise: true) }));
        var c = ev.Eval("ns_circle(SP1.S, 2.5, 7)", new Scope("t")).AsCube();
        Assert.Equal(new[] { "freq", "point" }, c.Axes.Select(a => a.Name));
        var pts = c.ComplexValues;
        for (int f = 0; f < 2; f++)
        {
            Assert.True((pts[f * 7] - pts[f * 7 + 6]).Magnitude < 1e-12);
            for (int k = 0; k < 7; k++)
                Assert.All(ev.Eval($"nf(SP1.S, ns_circle(SP1.S, 2.5, 7)[:, {k}])", new Scope("t")).AsCube().RealValues,
                           v => Assert.Equal(2.5, v, 1e-12));
        }
        Assert.All(ev.Eval("ns_circle(SP1.S, 0.5)", new Scope("t")).AsCube().ComplexValues, z => Assert.True(double.IsNaN(z.Real)));
        Assert.All(ev.Eval("nf(SP1.S, 1)", new Scope("t")).AsCube().RealValues, v => Assert.True(double.IsNaN(v)));

        double f50 = 1 + 2.5 / 5;    // 1 + Cs22/(kT0·|S21|²)
        Assert.All(ev.Eval("nf(SP1.S)", new Scope("t")).AsCube().RealValues, v => Assert.Equal(10 * Math.Log10(f50), v, 1e-12));
        Assert.All(ev.Eval("nf(SP1.S, 0, 2)", new Scope("t")).AsCube().RealValues, v => Assert.Equal(10 * Math.Log10(f50), v, 1e-12));
        Assert.All(ev.Eval("te(SP1.S)", new Scope("t")).AsCube().RealValues, v => Assert.Equal(NoiseMetrics.T0 * (f50 - 1), v, 1e-9));
    }

    /// <summary>ns_circle from the noise parameters alone draws the run's own circle: numbers, or one value per frequency
    /// (NFmin from the noise parameters in dB, rn = Rn/R), the cubes' axes leading.</summary>
    [Fact]
    public void NsCircle_FromNoiseParameters_IsTheRunsCircle()
    {
        // The run's parameters, as cubes the way a run reports them: NFmin in dB, Sopt.
        var ds = TwoPort(noise: true);
        var p = NoiseMetrics.FromWaves(At(ds["S"], 0), At(ds["Cs"], 0), [new(50, 0), new(50, 0)], 50);
        var freq = ds["S"].Axes[0];
        ds.Add("NFmin", new DataCube([freq], new[] { p.NFminDb, p.NFminDb }) { Unit = "dB" });
        ds.Add("Sopt", new DataCube([freq], new[] { p.GammaOpt, p.GammaOpt }));
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds }));
        var run = ev.Eval("ns_circle(SP1.S, 2.5, 9)", new Scope("t")).AsCube();
        Assert.All(ev.Eval("rn(SP1.S)", new Scope("t")).AsCube().RealValues, v => Assert.Equal(p.Rn / 50, v, 1e-12));

        var fromCubes = ev.Eval("ns_circle(2.5, SP1.NFmin, SP1.Sopt, rn(SP1.S), 9)", new Scope("t")).AsCube();
        Assert.Equal(new[] { "freq", "point" }, fromCubes.Axes.Select(a => a.Name));
        var fromNumbers = ev.Eval(FormattableString.Invariant(
            $"ns_circle(2.5, {p.NFminDb:R}, {p.GammaOpt.Real:R} + j*{p.GammaOpt.Imaginary:R}, {p.Rn / 50:R}, 9)"), new Scope("t")).AsCube();
        Assert.Equal(new[] { "point" }, fromNumbers.Axes.Select(a => a.Name));
        for (int k = 0; k < run.BufferLength; k++)
        {
            Assert.True((run.ComplexValues[k] - fromCubes.ComplexValues[k]).Magnitude < 1e-12, $"point {k}");
            Assert.True((run.ComplexValues[k % 9] - fromNumbers.ComplexValues[k % 9]).Magnitude < 1e-12, $"point {k}");
        }
    }

    private static NumFlat.Mat<Complex> At(DataCube c, int f)
    {
        var m = new NumFlat.Mat<Complex>(2, 2);
        for (int r = 0; r < 2; r++)
            for (int k = 0; k < 2; k++)
                m[r, k] = c.ComplexValues[f * 4 + r * 2 + k];
        return m;
    }
}
