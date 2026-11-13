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
    private static string Bench(double f2Mhz, string analysis, string sourceZ = "Z=50 Ohm") => $"""
        PnTone:S  in 0  Freq[1]=100 MHz  Pavl[1]=30 dBm  Phase[1]=0 deg  Freq[2]={f2Mhz} MHz  Pavl[2]=30 dBm  Phase[2]=0 deg  {sourceZ}
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

        // The second tone is in the single-tone run, not only the first: equal powers into a matched input read equal
        // carriers (comparing against a single-tone run cannot show this, since both would lack it alike).
        var c1 = TwoToneMeasurements.Tone(two, 0, "in", 1, 0).Magnitude;
        Assert.InRange(TwoToneMeasurements.Tone(two, 0, "in", 0, 1).Magnitude / c1, 0.999, 1.001);

        if (f2Mhz == 300.0)   // (1,3) is 1000 MHz: even order, where an odd nonlinearity puts nothing
            Assert.True(TwoToneMeasurements.Tone(two, 0, "thru", 1, 3).Magnitude < 1e-12);
    }

    /// <summary>A source's Z[k] bands are measured from the mean of the tones on the lattice, and the commensurate run
    /// must keep that ruler: it took the common fundamental, which put the 300 MHz tone in band 3 instead of band 2, so
    /// with Z[2] = 10 Ω the tone saw 50 Ω at 300 MHz and 10 Ω at 300.001 MHz (0.316 V against 0.236 V at the input).</summary>
    [Fact]
    public void ACommensurateRun_KeepsTheLatticesTerminationBands()
    {
        string withZ2 = "Z=50 Ohm  Z[2]=10 Ohm";
        var (exact, warnings) = Run(Bench(300.0, TwoTone(300.0), withZ2));
        Assert.Contains(warnings, w => w.Contains("harmonics of one fundamental", StringComparison.Ordinal));
        var (lattice, _) = Run(Bench(300.001, TwoTone(300.001), withZ2));
        foreach (var (k1, k2) in new[] { (1, 0), (0, 1) })
        {
            var a = TwoToneMeasurements.Tone(exact, 0, "in", k1, k2).Magnitude;
            var b = TwoToneMeasurements.Tone(lattice, 0, "in", k1, k2).Magnitude;
            Assert.True(Math.Abs(a - b) <= 1e-3 * b, $"({k1},{k2}): {a} at 300 MHz against {b} at 300.001 MHz");
        }
    }

    /// <summary>A commensurate point hands back its interface seed, and a later commensurate point warm-starts from it.</summary>
    [Fact]
    public void ACommensurateRun_ChainsItsSeed()
    {
        var (lib, tb) = new CnlReader().Read(Bench(300.0, TwoTone(300.0)));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals);
        var cold = new HbEngine(nl, tb).Run(p);
        Assert.NotNull(cold.InterfaceV);
        var warm = new HbEngine(nl, tb).Run(p, cold.InterfaceV);
        Assert.True(warm.Converged);
        int Iters(HbRunResult r) => r.Trace!.Steps.Sum(s => s.Iterations);
        Assert.True(Iters(warm) < Iters(cold), $"warm {Iters(warm)} iterations against cold {Iters(cold)}");
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
