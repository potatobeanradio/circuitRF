using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using CircuitRF.Render.DataDisplay;
using RfCore.Data;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Brief NZ-8: the Signal quality preset over an envelope result that measured EVM — the constellation on one scale, the
/// EVM per carrier and the spectrum — and the 1:1 axis option the constellation is drawn with.
/// </summary>
public sealed class SignalQualityPresetTests
{
    /// <summary>A matched pad driven by a clipped LTE carrier, its cubes filed under ENV1 as a run's are.</summary>
    private static DataSet Result(string carrier, string stop = "0.5e-3", string clip = "Clip=8")
    {
        var (lib, tb) = new CnlReader().Read($"""
            MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=50 Record=0.5e-3 {clip} Carrier[1]="{carrier}"
            R:R1 s 0 R=96.248 Ohm
            R:R2 s b R=71.151 Ohm
            R:R3 b 0 R=96.248 Ohm
            IProbe:Iout b c
            R:RL c 0 R=50 Ohm
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop={stop}
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var run = EnvelopeRunner.Run(plan, Elab);
        var grouped = new DataSet();
        foreach (var (name, cube) in run.Cubes) grouped.AddToGroup("ENV1", name, cube);
        return grouped;
    }

    /// <summary>
    /// Three plots: the constellation (Im against Re of the received and the ideal points, points only, one scale), the EVM
    /// table and the spectrum, every trace a spec that parses against the result. A result with no EVM offers none.
    /// </summary>
    [Fact]
    public void ThePreset_IsTheConstellationTheTableAndTheSpectrum()
    {
        var ds = Result("LTE;BW=5MHz;Mod=64QAM;Off=0;P=0;Seed=3");
        Assert.True(SignalQualityPreset.Available(ds));
        var plots = SignalQualityPreset.Build(ds, "amp.npy");
        Assert.Equal(3, plots.Count);

        var cons = plots[0];
        Assert.Equal(PlotType.Rect, cons.PlotType);
        Assert.True(cons.Axes!.EqualAspect);
        Assert.Equal(2, cons.Traces.Count);
        foreach (var t in cons.Traces)
        {
            Assert.False(t.Properties.LineEnabled);
            Assert.True(t.Properties.MarkerEnabled);
            Assert.True(CubeTraceSpecParser.TryParse(t.Expression!, ds, out _, out _, out var ty, out var e1), e1);
            Assert.True(CubeTraceSpecParser.TryParse(t.XSpec!, ds, out _, out _, out var tx, out var e2), e2);
            Assert.Equal((CubeTransform.Imag, CubeTransform.Real), (ty, tx));
        }

        Assert.Equal(PlotType.Table, plots[1].PlotType);
        Assert.Equal(["EVM %", "MER dB", "EVM peak %"], plots[1].Traces.Select(t => t.Label));
        Assert.Equal(PlotType.Rect, plots[2].PlotType);
        Assert.Contains("PSD", plots[2].Traces[0].Expression);

        Assert.False(SignalQualityPreset.Available(Result("CW;BW=0.2MHz;Off=0;P=0")));
    }

    /// <summary>
    /// FSK has no constellation (its envelope is constant): the picture is the frequency at each bit, received over sent,
    /// and the table carries the FSK error, deviation and offset. CSS has no picture of its own: the table and the spectrum.
    /// </summary>
    [Fact]
    public void FskDrawsItsFrequency_AndCssItsTableAndSpectrum()
    {
        var fsk = Result("FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0", clip: "");
        var plots = SignalQualityPreset.Build(fsk, "fsk.npy");
        Assert.Equal(3, plots.Count);
        Assert.All(plots[0].Traces, t => Assert.True(CubeTraceSpecParser.TryParse(t.Expression!, fsk, out _, out _, out _, out var e), e));
        Assert.Contains("FreqPts", plots[0].Traces[0].Expression);
        Assert.Equal(["FSK error %", "Deviation Hz", "Offset Hz"], plots.Single(p => p.PlotType == PlotType.Table).Traces.Select(t => t.Label));

        var css = Result("CSS;SF=7;BW=125kHz;Off=0;P=0", stop: "5e-3", clip: "");
        var two = SignalQualityPreset.Build(css, "css.npy");
        Assert.Equal([PlotType.Table, PlotType.Rect], two.Select(p => p.PlotType));
        Assert.Equal(["CSS error %"], two[0].Traces.Select(t => t.Label));
        Assert.True(double.IsFinite(css["ENV1.CSSerr_pct"].RealValues[0]));
    }

    /// <summary>
    /// Equal X and Y scale: one world unit is as many pixels along X as along Y, the plot box narrowed to keep it, the window
    /// untouched; off, the box is the plot's own. Off is not written to the .cdd, on is, and it reads back.
    /// </summary>
    [Fact]
    public void EqualAspect_DrawsOneUnitAlike_AndIsSavedOnlyWhenOn()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Axes.Window = new PlotRect(-1.5, -1.5, 3, 3);
        var canvas = (W: 640.0, H: 400.0);
        var off = PlotRenderer.BuildTransforms(plot, canvas);
        Assert.NotEqual(off.Primary.XScale, Math.Abs(off.Primary.YScale), 6);

        plot.Axes.EqualAspect = true;
        var on = PlotRenderer.BuildTransforms(plot, canvas);
        Assert.Equal(on.Primary.XScale, Math.Abs(on.Primary.YScale), 9);
        Assert.Equal(new PlotRect(-1.5, -1.5, 3, 3), plot.Axes.Window);
        Assert.True(on.Viewport.Width < off.Viewport.Width);

        Assert.DoesNotContain("EqualAspect", JsonSerializer.Serialize(new AxesConfig()));
        var json = JsonSerializer.Serialize(new AxesConfig { EqualAspect = true, WindowWidth = 3, WindowHeight = 3 });
        var back = new Plot(PlotType.Rect, FreqUnit.GHz);
        PlotConfigLoader.RestoreAxes(back, JsonSerializer.Deserialize<AxesConfig>(json));
        Assert.True(back.Axes.EqualAspect);
    }
}
