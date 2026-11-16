using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using RfCore.Propagation;
using RfCore.Statistics;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Brief PCH-10's gates through the Propagation Channel itself, one drop a channel built at that Drop (as a sweep over Drop elaborates
/// each point):
///   2  the channel takes its drop's draws: the shadowing is DropDraws' at the model's σ and lands in the excess loss, the line-of-sight
///      state is drawn under Los=auto with the model's probability (its fraction over 4,000 drops within the 99.9 % interval), and
///      Shadowing=off and a stated Los draw nothing;
///   5  flat Rayleigh BPSK over 4,000 drops: the average of each drop's BER, ½·erfc(√(γ|h|²)), equals ½(1 − √(γ/(1 + γ))) within four
///      standard errors at 0, 10 and 20 dB;
///   6  outage_over of the received power over 10⁴ drops at the Rayleigh 99 % margin (10·log10(−ln 0.99) below the mean) is 1 %, inside
///      its own binomial interval.
/// </summary>
public sealed class PropagationChannelDropTests(ITestOutputHelper output)
{
    private static PropagationChannelModel Channel(long drop, params (string Key, Value V)[] extra)
    {
        var p = new Dictionary<string, Value>(StringComparer.Ordinal) { ["Distance"] = new Value(120.0), ["Drop"] = new Value(drop), ["Seed"] = new Value(7.0) };
        foreach (var (k, v) in extra) p[k] = v;
        return new PropagationChannelModel(p, 25);
    }

    private static (string, Value) W(string k, string v) => (k, new Value(v));
    private static (string, Value) N(string k, double v) => (k, new Value(v));

    [Fact]
    public void Gate2_TheChannelTakesItsDropsShadowingAndLineOfSight()
    {
        const double f = 3.5e9;
        var umi = new[] { W("PathLoss", "38901"), W("Scenario", "UMi"), N("TxHeight", 10), N("RxHeight", 1.5) };
        double pLos = Tr38901.LosProbability(Scenario38901.UMi, 120, 10, 1.5);
        int los = 0;
        const int n = 4000;
        for (int d = 1; d <= n; d++)
        {
            var r = Channel(d, umi).At(f);
            var drop = r.Drop!;
            Assert.Equal(pLos, drop.LosProbability!.Value, 12);
            if (drop.Los!.Value) los++;
            // the shadowing is the drawn state's σ times this drop's normal draw, and it is in the excess loss
            Assert.Equal(DropDraws.Shadowing(drop.SigmaDb, 7, d, ""), drop.ShadowingDb, 12);
            Assert.Equal(r.LargeScale.LossDb - PropagationMath.FreeSpaceDb(f, r.DistanceM) + drop.ShadowingDb, r.ExcessDb, 9);
            Assert.Equal(drop.Los.Value ? 4 : 7.82, drop.SigmaDb, 12);
        }
        var (lo, hi) = BetaFunction.BinomialInterval(los, n, 0.999);
        output.WriteLine($"Los=auto drew line of sight in {los} of {n}: [{lo:G4}, {hi:G4}] against P_LOS {pLos:G5}");
        Assert.InRange(pLos, lo, hi);

        var off = Channel(3, [.. umi, W("Shadowing", "off"), W("Los", "nlos")]).At(f).Drop!;
        Assert.Equal(0, off.ShadowingDb);
        Assert.Null(off.Los);
    }

    /// <summary>R-pch10-1 — Rain=drawn on a 5 km terrestrial path: each drop's rain term is P.530's at the time percentage it drew, and
    /// none beyond P.530's 1 %, so about 1 % of drops see rain.</summary>
    [Fact]
    public void RainDrawn_IsTheAttenuationExceededForTheDrawnPercentage()
    {
        const int n = 2000;
        int rainy = 0;
        for (int d = 1; d <= n; d++)
        {
            var r = Channel(d, N("Distance", 5000), W("Rain", "drawn"), N("RainRate", 40)).At(20e9);
            double p = r.Drop!.RainTimePercent!.Value;
            Assert.Equal(DropDraws.RainTimePercent(7, d, ""), p, 15);
            double expected = p > 1 ? 0 : ItuP530.Rain(20, r.DistanceM / 1000, 40, Math.Max(p, 0.001), 45);
            var term = r.LargeScale.Terms.Single(t => t.Name.StartsWith("P.530 rain", StringComparison.Ordinal));
            Assert.Equal(expected, term.Db, 12);
            if (term.Db > 0) rainy++;
        }
        var (lo, hi) = BetaFunction.BinomialInterval(rainy, n, 0.999);
        output.WriteLine($"rain in {rainy} of {n} drops: [{lo:G3}, {hi:G3}]");
        Assert.InRange(0.01, lo, hi);
    }

    /// <summary>R-pch10-2 — Multipath=scenario: the channel's rays are its drop's TR 38.901 rays, its shadowing the drop's correlated SF
    /// (positive SF, more power, is less loss), and it refuses a model that is not 38901 and a spread the scenario draws itself.</summary>
    [Fact]
    public void ScenarioDrop_IsTheChannelsRays_AndItsShadowing()
    {
        var umi = new[] { W("PathLoss", "38901"), W("Scenario", "UMi"), N("TxHeight", 10), N("RxHeight", 1.5), W("Multipath", "scenario") };
        var r = Channel(4, umi).At(28e9);
        var sc = r.Drop!.Scenario!;
        Assert.Equal(sc.Rays.Rays.Count, r.Rays.Count);
        Assert.Equal(-sc.SfDb, r.Drop.ShadowingDb, 12);
        Assert.Equal(r.Drop.Los, sc.Los);
        Assert.Contains(r.Notes, n => n.Contains("scenario drop"));
        Assert.Throws<ParameterRefusalException>(() => Channel(4, W("Multipath", "scenario")));
        Assert.Throws<ParameterRefusalException>(() => Channel(4, [.. umi, N("DelaySpread", 100e-9)]));
    }

    /// <summary>R-pch10-3 — States=p681 on a satellite path: the line of sight's gain is P.681's (the state at t = 0 the drop's series' own,
    /// and the level moving as the terminal does), and it refuses a terrestrial model and a missing environment.</summary>
    [Fact]
    public void SatelliteStates_DriveTheLineOfSight()
    {
        var sat = new[] { W("PathLoss", "satellite"), N("Altitude", 35786), N("Elevation", 40), W("States", "p681"), W("StateEnvironment", "urban"), N("Velocity", 15) };
        var ch = Channel(9, sat);
        var r = ch.At(2.2e9);
        var states = r.Drop!.States!;
        Assert.True(r.Fades[0].Supplied);
        Assert.Equal("2.2 GHz / urban / 45°", states.Series.Set.Label);
        Assert.Equal(states.Gain(0), r.Fades[0].Gain(0));
        Assert.NotEqual(ch.H(2.2e9, 0), ch.H(2.2e9, 1.0));        // 15 m along the route, the level has moved
        Assert.Throws<ParameterRefusalException>(() => Channel(9, W("States", "p681"), W("StateEnvironment", "urban")));
        Assert.Throws<ParameterRefusalException>(() => Channel(9, W("PathLoss", "satellite"), W("States", "p681")));
    }

    /// <summary>|h|² of each drop's flat Rayleigh channel, normalised by the free-space gain (the fading has unit mean power).</summary>
    private static double[] RayleighPower(int drops)
    {
        double friis = PropagationMath.C / 2e9 / (4 * Math.PI * 120);
        return [.. Enumerable.Range(1, drops).Select(d =>
        {
            var h = Channel(d, W("Fading", "rayleigh"), N("fD", 0)).H(2e9) / friis;
            return h.Real * h.Real + h.Imaginary * h.Imaginary;
        })];
    }

    [Fact]
    public void Gate5_FlatRayleighBpsk_AveragedOverDrops()
    {
        var g2 = RayleighPower(4000);
        foreach (double snrDb in new[] { 0.0, 10, 20 })
        {
            double gamma = Math.Pow(10, snrDb / 10);
            var ber = g2.Select(x => 0.5 * Normal.Erfc(Math.Sqrt(gamma * x))).ToArray();
            double mean = ber.Average(), se = Math.Sqrt(ber.Select(b => (b - mean) * (b - mean)).Sum() / (ber.Length - 1) / ber.Length);
            double closed = 0.5 * (1 - Math.Sqrt(gamma / (1 + gamma)));
            output.WriteLine($"{snrDb} dB: average BER {mean:G5} ± {4 * se:G3} (4 s.e.) against {closed:G5}");
            Assert.True(Math.Abs(mean - closed) < 4 * se, $"{snrDb} dB: {mean} against {closed}");
        }
    }

    [Fact]
    public void Gate6_OutageAtTheRayleigh99PercentMargin_IsOnePercent()
    {
        var g2 = RayleighPower(10_000);
        var ds = new DataSet();
        ds.Add("prx", new DataCube([new Axis("drop", [.. Enumerable.Range(1, g2.Length).Select(i => (double)i)])], [.. g2.Select(x => 10 * Math.Log10(x))]) { Unit = "dB" });
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["MC"] = ds }));
        double margin = 10 * Math.Log10(-Math.Log(0.99));
        double outage = ev.Eval($"outage_over(MC.prx, {margin.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, \"drop\")", new Scope("t")).AsReal();
        var c = ev.TakeCompanions();
        double lo = c["lo"].RealValues[0], hi = c["hi"].RealValues[0];
        output.WriteLine($"outage at {margin:G5} dB: {outage:G4}, interval [{lo:G4}, {hi:G4}]");
        Assert.InRange(0.01, lo, hi);
        Assert.InRange(outage, lo, hi);
    }

    /// <summary>A drop whose metric is NaN (the receiver could not read it — often the deep fade the outage is about) is out of the
    /// fraction, but the interval takes it both ways, and the evaluation says how many there were. Dropped from both counts, 2 of 10
    /// below with 3 NaN read [0.025, 0.56] for 2/7, where those 3 may be outages.</summary>
    [Fact]
    public void OutageOver_ANaNDrop_WidensTheInterval_AndIsSaid()
    {
        double[] v = [-90, -100, double.NaN, -91, -101, double.NaN, -92, double.NaN, -93, -94];
        var ds = new DataSet();
        ds.Add("prx", new DataCube([new Axis("drop", [.. Enumerable.Range(1, v.Length).Select(i => (double)i)])], v) { Unit = "dB" });
        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["MC"] = ds }));
        double outage = ev.Eval("outage_over(MC.prx, -95, \"drop\")", new Scope("t")).AsReal();
        var c = ev.TakeCompanions();
        Assert.Equal(2.0 / 7, outage, 12);
        Assert.Equal(RfCore.Statistics.BetaFunction.BinomialInterval(2, 10).Low, c["lo"].RealValues[0], 12);
        Assert.Equal(RfCore.Statistics.BetaFunction.BinomialInterval(5, 10).High, c["hi"].RealValues[0], 12);
        Assert.Contains(ev.TakeNotes(), n => n.Contains("3 value(s) are NaN", StringComparison.Ordinal));
    }
}
