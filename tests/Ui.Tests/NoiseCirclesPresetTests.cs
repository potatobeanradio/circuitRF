using System.Numerics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Render.DataDisplay;
using RfCore.Data;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Brief NZ-2: the circle functions as ordinary Data Display expression traces — evaluated once over the result's own
/// analysis and drawn at the frequency the trace's Freq picker names — and the Noise and gain circles preset built of them.
/// </summary>
public sealed class NoiseCirclesPresetTests
{
    private static string NoiseDir()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "noise"))) return Path.Combine(dir, "testdata", "noise");
        throw new DirectoryNotFoundException("testdata/noise not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The testdata/noise <c>pozar_ex12_5</c> two-port with Noise=yes, filed under SP1 as a run's result is.</summary>
    private static DataSet Result()
    {
        var (lib, tb) = new CnlReader().Read("""
            Port:P1  p1 0  Num=1 Z=50.0 Ohm
            Port:P2  p2 0  Num=2 Z=50.0 Ohm
            SnP:X1  p1  p2  NumPorts=2 File="pozar_ex12_5.s2p"
            analysis SP1 type=sparam start=3.5 stop=4.5 npts=3 Unit=GHz Noise=yes
            """, sourceDirectory: NoiseDir());
        var nl = new Elaborator(lib).Elaborate(tb);
        var spa = tb.Analyses.OfType<SParameterAnalysis>().First();
        var run = SParameterEngine.Run(nl, spa.Expand(nl.ResolvedGlobals), AnalysisSettings.Default.WithNoiseOf(spa));
        var grouped = new DataSet();
        foreach (var (name, cube) in run.Cubes) grouped.AddToGroup("SP1", name, cube);
        return grouped;
    }

    private static (Complex[] Points, TraceExpression.FreqPin? Pin, IReadOnlyDictionary<string, DataCube> Companions) Trace(
        DataSet ds, string expr, int freqIndex)
    {
        Assert.True(TraceExpression.TryEvaluate(expr, ds, PlotType.Smith, freqIndex, out _, out var cz, out _, out _, out _, out _,
                                                out var companions, out var pin, out var err), err);
        return (cz!, pin, companions);
    }

    /// <summary>A circle expression on a trace is the measurement's circle at the picked frequency, point for point.</summary>
    [Fact]
    public void ACircleExpression_IsTheCircleAtThePickedFrequency()
    {
        var ds = Result();
        var one = new DataSet();
        foreach (var (n, c) in ds.CubesIn("SP1")) one.Add(n, c);
        var all = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = one }))
            .Eval("ga_circle(SP1.S, max_gain(SP1.S) - 1)", new Scope("t")).AsCube().ComplexValues;

        var (pts, pin, _) = Trace(ds, "ga_circle(SP1.S, max_gain(SP1.S) - 1)", 2);
        Assert.Equal(2, pin!.Index);
        Assert.Equal("freq", pin.Freq.Name);
        Assert.Equal(101, pts.Length);
        for (int k = 0; k < 101; k++) Assert.Equal(all[2 * 101 + k], pts[k]);
    }

    /// <summary>With no frequency asked for, a circle's [freq, point] is drawn at the first; a [freq] quantity asked at one
    /// frequency is that one point (Sopt as a marker).</summary>
    [Fact]
    public void ThePin_DefaultsToTheFirst_AndMakesAQuantityOnePoint()
    {
        var ds = Result();
        Assert.Equal(0, Trace(ds, "ns_circle(SP1.S, SP1.NFmin + 0.5)", -1).Pin!.Index);
        var (sopt, pin, _) = Trace(ds, "SP1.Sopt", 1);
        Assert.Equal(1, pin!.Index);
        Assert.Equal((Complex)ds["SP1.Sopt"][1], Assert.Single(sopt));
    }

    /// <summary>stab_circle leaves its stable side beside it, pinned with it, which the Smith chart shades by.</summary>
    [Fact]
    public void AStabilityCircle_CarriesItsStableSide()
    {
        var ds = Result();
        var one = new DataSet();
        foreach (var (n, c) in ds.CubesIn("SP1")) one.Add(n, c);
        var side = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = one }))
            .Eval("stab_side(SP1.S, \"source\")", new Scope("t")).AsCube().RealValues;
        for (int f = 0; f < 3; f++)
        {
            var (_, _, companions) = Trace(ds, "stab_circle(SP1.S, \"source\")", f);
            Assert.Equal(side[f], Assert.Single(companions[Evaluator.StableSideCompanion].RealValues));
        }
    }

    /// <summary>The preset: offered only on a two-port noise result; one Smith chart of Sopt, three NF circles, three
    /// available-gain circles and the source stability circle, every one an expression trace that resolves, at the middle
    /// frequency; and it survives a .cdd round trip with its frequency.</summary>
    [Fact]
    public void ThePreset_IsEightResolvingTraces_OnANoiseResultOnly()
    {
        var ds = Result();
        Assert.True(NoiseCirclesPreset.Available(ds));
        var plain = new DataSet();
        foreach (var (n, c) in ds.CubesIn("SP1")) if (n is "S" or "Z0") plain.AddToGroup("SP1", n, c);
        Assert.False(NoiseCirclesPreset.Available(plain));

        var pc = NoiseCirclesPreset.Build(ds, "run.npy")!;
        Assert.Equal(PlotType.Smith, pc.PlotType);
        Assert.Equal(8, pc.Traces.Count);
        Assert.Equal(3, pc.Traces.Count(t => t.Expression!.StartsWith("ns_circle(", StringComparison.Ordinal)));
        Assert.Equal(3, pc.Traces.Count(t => t.Expression!.StartsWith("ga_circle(", StringComparison.Ordinal)));
        Assert.Single(pc.Traces, t => t.Expression!.StartsWith("stab_circle(", StringComparison.Ordinal));
        Assert.Single(pc.Traces, t => t.Expression == "SP1.Sopt" && t.Properties.MarkerEnabled && !t.Properties.LineEnabled);
        foreach (var t in pc.Traces)
        {
            Assert.Equal(1, t.ExpressionFreqIndex);
            var (pts, pin, _) = Trace(ds, t.Expression!, t.ExpressionFreqIndex!.Value);
            Assert.Equal(1, pin!.Index);
            Assert.All(pts, z => Assert.True(double.IsFinite(z.Real) && double.IsFinite(z.Imaginary), t.Expression));
        }

        var back = JsonSerializer.Deserialize<PlotContainerConfig>(JsonSerializer.Serialize(pc, DataDisplayJson.Options), DataDisplayJson.Options)!;
        Assert.All(back.Traces, t => Assert.Equal(1, t.ExpressionFreqIndex));
    }
}
