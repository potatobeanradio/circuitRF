using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// The signal families through a matched pad, read at an IProbe: each family's quality readout survives the envelope run
/// and reads what the record the source plays reads (SignalQuality on the source's own record), as EnvelopeScTests' gate 3
/// does for the single carrier.
/// </summary>
public sealed class EnvelopeFamiliesTests(ITestOutputHelper output)
{
    private static (DataSet Ds, WaveformQuality Own) Pad(string carrier, string stop)
    {
        string cnl = $"""
            MOD_Source:S1 s 0 Fc=2.4e9 Pavg=-30 Z=1e-3 Carrier[1]="{carrier}"
            R:RS s a R=50 Ohm Noise=no
            R:R1 a 0 R=96.248 Ohm Noise=no
            R:R2 a b R=71.151 Ohm Noise=no
            R:R3 b 0 R=96.248 Ohm Noise=no
            IProbe:Iout b c
            R:RL c 0 R=50 Ohm Noise=no
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop={stop}
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        var sig = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single().Signal;
        var rec = sig.Carriers[0];
        return (ds, SignalQuality.Evaluate(SignalQuality.Receive(rec, sig.RecordSeconds, rec.Native, rec.NativeRateHz, 0, 0, out _)!));
    }

    [Fact]
    public void Wlan_EvmSurvivesThePad()
    {
        var (ds, own) = Pad("WLAN;Std=ac;BW=20MHz;MCS=7;Symbols=6;Oversample=4;Off=0;P=0;Seed=1", "250e-6");
        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"envelope {evm:G6} %, source record {own.ErrorPercent:G6} %");
        Assert.Equal(own.ErrorPercent, evm, 0.005);
        Assert.Equal(3, ds["Const"].Rank);
    }
}
