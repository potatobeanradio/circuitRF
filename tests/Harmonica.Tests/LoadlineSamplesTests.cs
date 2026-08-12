// ================================================================
//  LoadlineSamplesTests.cs — §7 (R-h9b-13) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class LoadlineSamplesTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT, coefficients folded in so the fixture needs no globals.</summary>
    private static CircuitModel Model(int loadlineSamples = 64) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
            LoadlineSamples = loadlineSamples,
        },
    };

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    private static Complex[,] ConvergedV(CircuitModel model, out HarmonicaContext ctx)
    {
        ctx = HarmonicaContext.Create(model, Settings);
        var r = PinSearch.Run(ctx, Terms(model));
        Assert.True(r.AtCompression is not null, "the fixture must reach compression to have a point to loadline");
        var at = r.Steps[^1];
        return at.Point.V;
    }

    [Fact]
    public void DefaultSampleCount_Is64()
        => Assert.Equal(64, new HarmonicaSettings().LoadlineSamples);

    // ══ R-h9b-13's own claim: exact at ANY sample count, not interpolated ═══════════════════

    [Fact]
    public void ResampledLoadline_IsExact_NotInterpolated_AtEveryDensity()
    {
        // R-h9b-13's own claim: the spectrum carries every harmonic, so evaluating it at ANY sample
        // count is EXACT rather than an interpolation of some other grid's samples. The public-API
        // oracle: 64 and 256 = 4×64 share every 4th time instant EXACTLY (θ = 2π·i/64 = 2π·(4i)/256),
        // so the SAME continuous curve evaluated at those shared instants must agree to numerical
        // precision — an interpolation scheme (fitting a curve THROUGH the 64 samples and reading it
        // at 256 points) would not reproduce this to full precision in general.
        var model = Model();
        var v = ConvergedV(model, out var ctx);
        int k = model.Settings.HarmonicCount;

        var (vds64, ids64) = IntrinsicPlane.Loadline(
            ctx.DutComponent, v, ctx.Interface.DeviceNodes, k, 64,
            ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);
        var (vds256, ids256) = IntrinsicPlane.Loadline(
            ctx.DutComponent, v, ctx.Interface.DeviceNodes, k, 256,
            ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);

        double maxVdsErr = 0, maxIdsErr = 0;
        for (int i = 0; i < 64; i++)
        {
            maxVdsErr = Math.Max(maxVdsErr, Math.Abs(vds64[i] - vds256[4 * i]));
            maxIdsErr = Math.Max(maxIdsErr, Math.Abs(ids64[i] - ids256[4 * i]));
        }

        output.WriteLine($"max |ΔVds| = {maxVdsErr:E3} V, max |ΔIds| = {maxIdsErr:E3} A " +
                         "at the 64 shared time instants between the 64- and 256-sample loadlines");
        Assert.True(maxVdsErr < 1e-9, $"64- and 256-sample loadlines disagree at a shared instant by {maxVdsErr:E3} V");
        Assert.True(maxIdsErr < 1e-9, $"64- and 256-sample loadlines disagree at a shared instant by {maxIdsErr:E3} A");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(333)]     // deliberately NOT a power of two — HbFft.Inverse could never take this
    public void Loadline_ProducesExactlyTheRequestedSampleCount_AtAnyDensity(int sampleCount)
    {
        var model = Model();
        var v = ConvergedV(model, out var ctx);
        int k = model.Settings.HarmonicCount;

        var (vds, ids) = IntrinsicPlane.Loadline(
            ctx.DutComponent, v, ctx.Interface.DeviceNodes, k, sampleCount,
            ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);

        Assert.Equal(sampleCount, vds.Length);
        Assert.Equal(sampleCount, ids.Length);
        Assert.All(vds, x => Assert.True(double.IsFinite(x)));
        Assert.All(ids, x => Assert.True(double.IsFinite(x)));
    }

    // ══ measured per-frame cost, reported rather than assumed ═══════════════════════════════

    [Fact]
    public void MeasuredCost_64SamplesVsTheOldSolveGrid()
    {
        var model = Model();
        var v = ConvergedV(model, out var ctx);
        int k = model.Settings.HarmonicCount;
        int gridN = HbFft.GridSize(k, model.Settings.FftOverSample);

        double Time(int n)
        {
            // Warm up once (JIT), then take a best-of-5 minimum — this repo's own convention for a
            // small, non-Benchmark-tagged measurement.
            IntrinsicPlane.Loadline(ctx.DutComponent, v, ctx.Interface.DeviceNodes, k, n,
                                    ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);
            double best = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                IntrinsicPlane.Loadline(ctx.DutComponent, v, ctx.Interface.DeviceNodes, k, n,
                                        ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        double at64 = Time(64);
        double atGridN = Time(gridN);
        output.WriteLine($"gridN (old) = {gridN} samples: {atGridN:F4} ms; 64 samples: {at64:F4} ms " +
                         $"— {at64 / Math.Max(atGridN, 1e-6):F2}× (K={k})");
    }

    // ══ CharmIo round trip + clamping ════════════════════════════════════════════════════════

    [Fact]
    public void RoundTripsThroughCharm_AndAnOlderCharmOpensAt64()
    {
        var model = Model(loadlineSamples: 200);
        var terms = Terms(model);
        string json = CharmIo.Write(model, terms);
        var (back, _) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);
        Assert.Empty(unresolved);
        Assert.Equal(200, back.Settings.LoadlineSamples);

        var (older, _) = CharmIo.Read("""{ "FormatVersion": 1 }""", null, out _, withMarkers: true);
        Assert.Equal(64, older.Settings.LoadlineSamples);
    }
}
