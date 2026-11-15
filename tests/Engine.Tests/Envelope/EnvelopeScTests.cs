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
/// Brief PCH-5, gate 3 (the pad): an SC carrier played through a matched pad and read at an IProbe reads the source
/// record's own EVM — the Signal Designer's, SignalQuality on the record the source plays — and decides every bit right,
/// with no equalizer and with the pilot equalizer trained on a preamble and pilot blocks. To 1e-5 percentage points, as
/// NZ-8's gate 1 is: the run plays the record interpolated to the envelope rate by a windowed sinc. The 3.3-symbol delay
/// half of the gate is ScQamTests.S3.
/// </summary>
public sealed class EnvelopeScTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Mod=QPSK", "")]
    [InlineData("Mod=16APSK;Preamble=32;PilotBlock=16/200", "Equalizer=pilot")]
    public void Gate3_APad_ReadsTheSourcesOwnEvm(string fields, string env)
    {
        string cnl = $"""
            MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=1e-3 Carrier[1]="SC;Rs=5Msps;{fields};RollOff=0.35;Data=PN9;Off=0;P=0"
            R:RS s a R=50 Ohm Noise=no
            R:R1 a 0 R=96.248 Ohm Noise=no
            R:R2 a b R=71.151 Ohm Noise=no
            R:R3 b 0 R=96.248 Ohm Noise=no
            IProbe:Iout b c
            R:RL c 0 R=50 Ohm Noise=no
            analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=1e-6 Stop=250e-6 {env}
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        var sig = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().Single().Signal;
        var rec = sig.Carriers[0];
        var own = SignalQuality.Evaluate(SignalQuality.Receive(rec, sig.RecordSeconds, rec.Native, rec.NativeRateHz, 0, 0, out _)!);

        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"envelope {evm:G6} %, source record {own.ErrorPercent:G6} %, bits {ds["Bits"].RealValues[0]}");
        Assert.Equal(own.ErrorPercent, evm, 1e-5);
        Assert.Equal(0, ds["BitErrors"].RealValues[0]);
        Assert.True(ds["Bits"].RealValues[0] > 1000);
        Assert.Equal(3, ds["Const"].Rank);
    }
}
