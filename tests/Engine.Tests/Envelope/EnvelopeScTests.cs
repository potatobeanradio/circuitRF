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

    /// <summary>
    /// Brief PCH-12: a single carrier offset by a line-of-sight Doppler is read at that offset and decided right. 500 Hz is a
    /// tenth of a bin over the 0.2 ms record: it was fitted as 0 Hz, because the delay fit took a delay a whole record away and
    /// its circular shift put the offset's phase ramp's jump mid-window. 100 kHz is twenty bins: past the fine fit, found by
    /// the power spectra's cross-correlation first (SignalQuality.CoarseOffset). A low-orbit downlink's 400 kHz read BER 0.5.
    /// </summary>
    [Theory]
    [InlineData(7.5)]
    [InlineData(1500.0)]
    public void Doppler_IsAcquired_AndTheSymbolsDecidedRight(double velocity)
    {
        string cnl = $"""
            MOD_Source:S1 tx 0 Fc=20 GHz Pavg=0 dBm Z=50 Ohm Carrier[1]="SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;Data=PN9;Preamble=64;PilotBlock=36/1440;Off=0;P=0"
            PropagationChannel:CH1 tx rx Distance=1000 TxVel="{velocity.ToString(System.Globalization.CultureInfo.InvariantCulture)},0,0" TxGain=40 RxGain=40
            IProbe:Irx rx o
            Port:PO o 0 Num=1 Z=50 Ohm
            analysis ENV1 type=env Stop=0.2e-3 Accuracy=draft MaxHarm=1 SaveWaveforms=no Equalizer=pilot
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        double doppler = ds["link:CH1:Doppler0_Hz"].RealValues[0], cfo = ds["CFO_Hz"].RealValues[0];
        output.WriteLine($"Doppler {doppler:G9} Hz, read {cfo:G9} Hz, EVM {ds["EVM_pct"].RealValues[0]:G4} %, bit errors {ds["BitErrors"].RealValues[0]}");
        Assert.Equal(doppler, cfo, 0.1);
        Assert.Equal(0, ds["BitErrors"].RealValues[0]);
        Assert.True(ds["EVM_pct"].RealValues[0] < 0.01);
    }

    /// <summary>The coarse offset with no preamble, a window one or two records long: a periodic PN record leaves the power spectrum
    /// nearly flat line by line, and correlating its detail read QPSK at 73 kHz as 1.9 MHz (EVM 256 %). The delay is found from the
    /// power envelopes, which an offset does not touch, and the offset from x·conj(s) at that delay.</summary>
    [Theory]
    [InlineData("QPSK", 2900.0, 77e-6)]
    [InlineData("BPSK", 1100.0, 110e-6)]
    public void Doppler_IsAcquired_WithoutAPreamble(string mod, double velocity, double stop)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string cnl = $"""
            MOD_Source:S1 tx 0 Fc=20 GHz Pavg=0 dBm Z=50 Ohm Carrier[1]="SC;Rs=10Msps;Mod={mod};RollOff=0.35;Data=PN9;Off=0;P=0"
            PropagationChannel:CH1 tx rx Distance=1000 TxVel="{velocity.ToString(inv)},0,0" TxGain=40 RxGain=40
            IProbe:Irx rx o
            Port:PO o 0 Num=1 Z=50 Ohm
            analysis ENV1 type=env Stop={stop.ToString("R", inv)} Accuracy=draft MaxHarm=1 SaveWaveforms=no Equalizer=genie
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        var ds = EnvelopeRunner.Run(plan, Elab);
        double doppler = ds["link:CH1:Doppler0_Hz"].RealValues[0], cfo = ds["CFO_Hz"].RealValues[0];
        output.WriteLine($"{mod}: Doppler {doppler:G9} Hz, read {cfo:G9} Hz, bit errors {ds["BitErrors"].RealValues[0]}");
        Assert.Equal(doppler, cfo, 0.1);
        Assert.Equal(0, ds["BitErrors"].RealValues[0]);
    }
}
