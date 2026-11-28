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
    private static (DataSet Ds, WaveformQuality? Own) Pad(string carrier, string stop)
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
        var own = SignalQuality.Receive(rec, sig.RecordSeconds, rec.Native, rec.NativeRateHz, 0, 0, out _);
        return (ds, own is null ? null : SignalQuality.Evaluate(own));
    }

    [Fact]
    public void Wlan_EvmSurvivesThePad()
    {
        var (ds, own) = Pad("WLAN;Std=ac;BW=20MHz;MCS=7;Symbols=6;Oversample=4;Off=0;P=0;Seed=1", "250e-6");
        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"envelope {evm:G6} %, source record {own!.ErrorPercent:G6} %");
        Assert.Equal(own!.ErrorPercent, evm, 0.005);
        Assert.Equal(3, ds["Const"].Rank);
    }

    [Fact]
    public void Gnss_CorrelationSurvivesThePad()
    {
        var (ds, own) = Pad("GNSS;Signal=E1;Sats=3@1kHz@100,11@-1.5kHz@2000@-2dB;CN0=48;Record=4ms;Off=0;P=0;Seed=3", "4.5e-3");
        var cn0 = ds["GnssCN0_dBHz"].RealValues;
        var phase = ds["GnssCodePhase_chips"].RealValues;
        output.WriteLine($"C/N0 {cn0[0]:0.###} / {cn0[1]:0.###} dB-Hz (record {own!.Gnss![0].Cn0DbHz:0.###} / {own.Gnss[1].Cn0DbHz:0.###}), code phase {phase[0]} / {phase[1]}");
        Assert.Equal(own.Gnss[0].Cn0DbHz, cn0[0], 0.1);
        Assert.Equal(own.Gnss[1].Cn0DbHz, cn0[1], 0.1);
        Assert.Equal([own.Gnss[0].CodePhaseChips, own.Gnss[1].CodePhaseChips], phase);
    }

    [Fact]
    public void WcdmaTm1_EvmAndCodeDomainPowerSurviveThePad()
    {
        var (ds, own) = Pad("WCDMA;Model=Tm1x64;Off=0;P=0", "10.2e-3");
        double evm = ds["EVM_pct"].RealValues[0], pcde = ds["PCDE_dB"].RealValues[0];
        var cdp = ds["CDP_dB"].RealValues;
        output.WriteLine($"EVM {evm:G4} % (record {own!.ErrorPercent:G4} %), PCDE {pcde:0.0} dB, CPICH {cdp[1]:0.000} dB (record {own!.Wcdma!.Channels[1].PowerDb:0.000})");
        Assert.True(evm < 0.1, $"EVM {evm} %");
        Assert.True(pcde < -50, $"PCDE {pcde} dB");
        Assert.Equal(own.Wcdma.Channels.Length, cdp.Length);
        for (int i = 0; i < cdp.Length; i++) Assert.Equal(own.Wcdma.Channels[i].PowerDb, cdp[i], 0.01);
    }

    /// <summary>A rotated, a differential and a burst single carrier decide every bit right: the rotation turned back first,
    /// the differential one on its steps (π/2-BPSK read straight lands on the decision boundary). The EDGE-like burst is read
    /// against its clean waveform through a band filter, so its run holds whole records.</summary>
    [Theory]
    [InlineData("Mod=BPSK;Rotation=90", "250e-6")]
    [InlineData("Mod=QPSK;Differential=1", "250e-6")]
    [InlineData("Mod=8PSK;Symbols=244;Preamble=26;Pulse=LinearGMSK;Rotation=67.5;FrameData=122;Guard=8.25;Ramp=4", "300e-6")]
    public void Sc_RotatedDifferentialAndBurst_DecideEveryBit(string fields, string stop)
    {
        var (ds, own) = Pad($"SC;Rs=5Msps;{fields};Off=0;P=0", stop);
        double evm = ds["EVM_pct"].RealValues[0];
        output.WriteLine($"EVM {evm:G6} % (record {own!.ErrorPercent:G6} %), bits {ds["Bits"].RealValues[0]}, errors {ds["BitErrors"].RealValues[0]}");
        Assert.Equal(own!.ErrorPercent, evm, 0.01);
        Assert.Equal(0, ds["BitErrors"].RealValues[0]);
        Assert.True(ds["Bits"].RealValues[0] > 500);
    }

    /// <summary>A Barker-coded pulse's compression survives the pad; a staggered train's pulses are each measured.</summary>
    [Fact]
    public void Radar_CompressionAndTrainPulses()
    {
        var (ds, own) = Pad("PCW;PRI=100us;PW=13us;Rise=0.1us;Fall=0.1us;Code=Barker:13;Off=0;P=0", "250e-6");
        double psl = ds["PSL_dB"].RealValues[0];
        output.WriteLine($"PSL {psl:0.000} dB (record {own!.Compression!.PslDb:0.000}), ISL {ds["ISL_dB"].RealValues[0]:0.000} dB");
        Assert.Equal(own.Compression.PslDb, psl, 0.01);
        var (train, _) = Pad("PCW;PRI=100us;PW=10us;PRIs=100us,125us,150us;Off=0;P=0", "760e-6");
        Assert.Equal(6, train["PulseTop_dBm"].RealValues.Length);
    }
}
