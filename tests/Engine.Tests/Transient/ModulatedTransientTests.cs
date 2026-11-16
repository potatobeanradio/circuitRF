using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>Brief MTR-1/MTR-2 gates: a MOD_Source played in native transient, kept at RF rate and at baseband.</summary>
public sealed class ModulatedTransientTests(ITestOutputHelper output)
{
    private const string Bench = """
        MOD_Source:S1 out 0 Fc=1 GHz Pavg=0 Z=50 Carrier[1]="LTE;BW=5MHz"
        IProbe:IP1 out load
        R:RL load 0 R=50

        """;

    private static (DataSet Ds, ElaboratedNetlist Nl) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals);
        return (TransientRunner.Run(p, nl), nl);
    }

    /// <summary>Gate MTR-1.1/1.2 — at RF rate the load voltage is ½·Re{Vs·b(τ)·e^(jωt)}, τ = RecordStart + t.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-6)]
    public void TheDrive_IsTheRecordOnTheCarrier(double recordStart)
    {
        var (ds, nl) = Run(Bench.Replace("Carrier[1]", $"RecordStart={recordStart:R} Carrier[1]") + "analysis TR1 type=tran Stop=1u Step=50p MaxStep=10p Store=rf\n");
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        var v = ds["V"];
        var t = v.Axes[0].Values;
        int node = Array.IndexOf(v.Axes[1].Labels!, "load");
        double rate = 1 / (t[1] - t[0]);
        long first = (long)Math.Round(recordStart * rate);
        Assert.True(Math.Abs(first - recordStart * rate) < 1e-6, "the reference grid lands on RecordStart");
        var b = src.Signal.CompositeWindow(rate, 0, first, t.Length);
        double worst = 0;
        for (int k = 0; k < t.Length; k++)
        {
            double want = 0.5 * (src.VsPeak * b[k] * Complex.FromPolarCoordinates(1, 2 * Math.PI * src.FcHz * t[k])).Real;
            worst = Math.Max(worst, Math.Abs(v.RealValues[k * v.Axes[1].Length + node] - want));
        }
        output.WriteLine($"RecordStart {recordStart}: worst {worst / src.VsPeak:E3} of Vs over {t.Length} points");
        Assert.True(worst < 1e-3 * src.VsPeak, $"worst {worst / src.VsPeak:E3} of Vs");
    }

    /// <summary>Gate MTR-2.1/2.2 — baseband storage: the load's harmonic-1 envelope is ½·Vs·b(τ), the others are empty, and the
    /// delivered power is Pavg into the matched load (over the window's own mean power).</summary>
    [Fact]
    public void Baseband_TheEnvelopesAreTheRecord_AndThePowerIsPavg()
    {
        var (ds, nl) = Run(Bench + "analysis TR1 type=tran Stop=3u\n");
        var src = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single();
        Assert.True(ds.CubesIn(DataSet.DefaultGroup).ContainsKey("__TranStore"));
        var v = ds["V"];
        Assert.Equal(["time", "node", "harmonic"], v.Axes.Select(a => a.Name));
        var t = v.Axes[0].Values;
        int nn = v.Axes[1].Length, nk = v.Axes[2].Length, node = Array.IndexOf(v.Axes[1].Labels!, "load");
        double rEnv = 1 / ds["__EnvStep_s"].RealValues[0];
        var b = src.Signal.CompositeWindow(rEnv, 0, (long)Math.Round(t[0] * rEnv), t.Length);
        double worst1 = 0, worstOther = 0;
        for (int k = 0; k < t.Length; k++)
            for (int h = 0; h < nk; h++)
            {
                var x = v.ComplexValues[(k * nn + node) * nk + h];
                if (h == 1) worst1 = Math.Max(worst1, (x - 0.5 * src.VsPeak * b[k]).Magnitude);
                else worstOther = Math.Max(worstOther, x.Magnitude);
            }
        double pavg = ds["Pavg_dBm"].RealValues[0];
        output.WriteLine($"{t.Length} envelope samples at {rEnv:G4} Hz; harmonic 1 worst {worst1 / src.VsPeak:E3}, others {worstOther / src.VsPeak:E3} of Vs; Pavg {pavg:F4} dBm");
        Assert.True(worst1 < 1e-3 * src.VsPeak, $"harmonic 1: {worst1 / src.VsPeak:E3} of Vs");
        Assert.True(worstOther < 1e-3 * src.VsPeak, $"harmonics 0, 2, 3: {worstOther / src.VsPeak:E3} of Vs");
        // into the matched load the delivered power is Pavg times the window's own mean |b|² (a few µs of OFDM is not the record's
        // unit mean)
        double window = 10 * Math.Log10(b.Average(z => z.Real * z.Real + z.Imaginary * z.Imaginary));
        Assert.True(Math.Abs(pavg - window) < 0.05, $"Pavg {pavg} dBm against the window's {window} dBm into the matched load");
    }

    /// <summary>Gate MTR-1.3 — harmonic balance reads neither RecordStart nor Ramp: bit for bit the same result.</summary>
    [Fact]
    public void Hb_IsUnchanged_ByTheTransientKeys()
    {
        DataSet Hb(string bench)
        {
            var (lib, tb) = new CnlReader().Read(bench + "analysis HB1 type=hb Tone=1 ToneUnit=GHz MaxHarm=3\n");
            var nl = new Elaborator(lib).Elaborate(tb);
            return new CircuitRF.Engine.HarmonicBalance.HbEngine(nl, tb).Run(
                CircuitRF.Engine.HarmonicBalance.HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));
        }
        var plain = Hb(Bench)["V"].ComplexValues;
        var keyed = Hb(Bench.Replace("Carrier[1]", "RecordStart=3u Ramp=10n Carrier[1]"))["V"].ComplexValues;
        Assert.Equal(plain, keyed);
    }

    /// <summary>Gate MTR-2.4 — RF storage of a modulated bench with an automatic Step resolves the carrier: the [time, node]
    /// cube on a grid of at most 1/(20 Fc).</summary>
    [Fact]
    public void Rf_AnAutomaticStep_ResolvesTheCarrier()
    {
        var (ds, _) = Run(Bench + "analysis TR1 type=tran Stop=50n Store=rf\n");
        var v = ds["V"];
        Assert.Equal(["time", "node"], v.Axes.Select(a => a.Name));
        var t = v.Axes[0].Values;
        Assert.True(t[1] - t[0] <= 1 / (20 * 1e9), $"step {t[1] - t[0]:G4} s");
    }

    /// <summary>Gate MTR-2.3 — baseband with no MOD_Source, and two sources at different Fc, are refused by name.</summary>
    [Fact]
    public void Baseband_WithoutOneCentreFrequency_IsRefused()
    {
        var none = Assert.Throws<TransientRefusedException>(() =>
            Run("Vdc:V1 a 0 Vdc=1\nR:R1 a 0 R=1\nanalysis TR1 type=tran Stop=1u Store=baseband\n"));
        Assert.Contains("has none", none.Message);
        var two = Assert.Throws<TransientRefusedException>(() => Run(Bench
            + "MOD_Source:S2 out2 0 Fc=1.5 GHz Pavg=0 Z=50 Carrier[1]=\"LTE;BW=5MHz\"\nR:R2 out2 0 R=50\nanalysis TR1 type=tran Stop=1u\n"));
        Assert.Contains("differ in Fc", two.Message);
    }
}
