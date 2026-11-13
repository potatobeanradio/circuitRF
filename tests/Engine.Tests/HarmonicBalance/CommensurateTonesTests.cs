using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>Issue 65 §1: two tones commensurate within the mixing order run as single-tone HB on their common
/// fundamental, read onto the mixing indices, with a warning that says so. An in-phase coupler with an odd tanh PIM:
/// the lattice read 1.00e-3 V at (1,3) for 100/300 MHz, an even-order product the nonlinearity cannot make.</summary>
public sealed class CommensurateTonesTests
{
    private static string Bench(double f2Mhz, string analysis) => $"""
        PnTone:S  in 0  Freq[1]=100 MHz  Pavl[1]=30 dBm  Phase[1]=0 deg  Freq[2]={f2Mhz} MHz  Pavl[2]=30 dBm  Phase[2]=0 deg  Z=50 Ohm
        Coupler:H  in 0  thru 0  cpl 0  iso 0  Coupling=3.0103 dB  Phase=0 deg  Z0=50 Ohm  PIM=-60 dBm  PIMPc=30 dBm
        R:R2  thru 0  R=50 Ohm
        R:R3  cpl 0  R=50 Ohm
        R:R4  iso 0  R=50 Ohm
        {analysis}
        """;

    private static string TwoTone(double f2Mhz) =>
        $"analysis HB1 type=hb NumFreqs=2 Tone[1]=100e6 Tone[2]={f2Mhz}e6 MaxMixOrder=5 MaxHarm=5 Tol=1e-12";

    private static (DataSet ds, List<string> warnings) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ds = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));
        return (ds, [.. nl.Warnings]);
    }

    private static Complex At(DataSet ds, string node, int harmonicOrMix, bool mix)
    {
        var v = ds["V"];
        var labels = v.Axes[0].Labels!;
        int n = Array.IndexOf(labels, node), M = v.Axes[1].Length;
        return v.ComplexValues[n * M + harmonicOrMix];
    }

    [Theory]
    [InlineData(300.0, 15)]
    [InlineData(200.0, 10)]
    public void CommensurateTones_RunOnTheCommonFundamental_ReadOntoTheMixingIndices_AndSaySo(double f2Mhz, int k)
    {
        var (two, warnings) = Run(Bench(f2Mhz, TwoTone(f2Mhz)));
        var warning = Assert.Single(warnings, w => w.Contains("harmonics of one fundamental", StringComparison.Ordinal));
        Assert.Contains("one fundamental, 100 MHz", warning, StringComparison.Ordinal);
        Assert.Contains($"to harmonic {k}", warning, StringComparison.Ordinal);

        var (one, _) = Run(Bench(f2Mhz, $"analysis HB1 type=hb Tone=100e6 ToneUnit=Hz MaxHarm={k} Tol=1e-12"));
        var mix = two["V"].Axes[1];
        Assert.Equal("mixIndex", mix.Name);
        foreach (var node in new[] { "in", "thru", "cpl" })
            for (int m = 0; m < mix.Length; m++)
            {
                int h = (int)Math.Round(Math.Abs(mix.Values[m]) / 100e6);
                var want = At(one, node, h, mix: false);
                if (mix.Values[m] < 0) want = Complex.Conjugate(want);
                var got = At(two, node, m, mix: true);
                Assert.True((got - want).Magnitude <= 1e-6 * Math.Max(want.Magnitude, 1e-9),
                            $"{node} {mix.Labels![m]}: {got} against harmonic {h}'s {want}");
            }

        if (f2Mhz == 300.0)   // (1,3) is 1000 MHz: even order, where an odd nonlinearity puts nothing
            Assert.True(TwoToneMeasurements.Tone(two, 0, "thru", 1, 3).Magnitude < 1e-12);
    }

    [Fact]
    public void IncommensurateTones_AreAnOrdinaryMultiToneRun()
    {
        var p = new HbAnalysisParams([100e6, 310e6], 5, 5, 1, 1e-12, Engine.DcBiasSteppingMode.IfNecessary, 0, null, 0, 0, 1);
        Assert.Null(HbEngine.Commensurate(p, int.MaxValue));
        var (_, warnings) = Run(Bench(310.0, TwoTone(310.0)));
        Assert.DoesNotContain(warnings, w => w.Contains("harmonics of one fundamental", StringComparison.Ordinal));
    }
}
