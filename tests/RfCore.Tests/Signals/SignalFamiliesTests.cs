// The signal families wired through the MOD_Source: each one's netlist form, its record through SignalComposer, and its
// quality read back from the record the source plays (SignalQuality.Receive / Evaluate, as a probe's envelope is read).
//
//   I1  Carriers that existed before the families format and cache exactly as they did.
//   W*  Wi-Fi (WLAN).
//   G*  GNSS.

using System;
using System.Linq;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class SignalFamiliesTests
{
    [Theory]
    [InlineData("LTE;BW=20MHz;Mod=64QAM;Off=0;P=0;Seed=1", "164cacb17186887373272bed")]
    [InlineData("NR;BW=100MHz;SCS=30kHz;Mod=256QAM;Off=-200MHz;P=-3;Seed=3", "99fb6a75317e802a81990f5e")]
    [InlineData("FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0", "ea3c644105dabc5fe9f614bd")]
    [InlineData("SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;Rings=2.85;Phases=45,15;Data=PN15;Preamble=64;PilotBlock=36/1440;Off=0;P=0", "0270996cc5df706316121ea4")]
    [InlineData("PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Off=0;P=0", "4804f3c9824e33398795a7b4")]
    public void I1_ExistingCarriers_FormatAndCacheAsBefore(string text, string key)
    {
        var c = SignalSpec.ParseCarrier(text);
        Assert.Equal(text, SignalSpec.FormatCarrier(c));
        Assert.Equal(key, new SignalSpec { Carriers = [c] }.CacheKey());
    }

    /// <summary>Parses, writes back to itself, survives the .cmod JSON; the refusals name the field.</summary>
    private static CarrierSpec RoundTrip(string text, CarrierStandard standard)
    {
        var c = SignalSpec.ParseCarrier(text);
        Assert.Equal(standard, c.Standard);
        Assert.Equal(text, SignalSpec.FormatCarrier(c));
        Assert.Equal(SignalSpec.FormatCarrier(c), SignalSpec.FormatCarrier(SignalSpec.FromJson(new SignalSpec { Carriers = [c] }.ToJson()).Carriers.Single()));
        return c;
    }

    private static void Refuses(params (string Text, string Fragment)[] cases)
    {
        foreach (var (text, fragment) in cases)
            Assert.Contains(fragment, Assert.ThrowsAny<FormatException>(() => SignalSpec.ParseCarrier(text)).Message, StringComparison.Ordinal);
    }

    /// <summary>The record the source plays, read back as a probe's envelope is.</summary>
    private static WaveformQuality Own(ModulatedSignal sig)
    {
        var rec = sig.Carriers.Single();
        var w = SignalQuality.Receive(rec, sig.RecordSeconds, rec.Native, rec.NativeRateHz, 0, 0, out string? why);
        Assert.True(w is not null, why);
        return SignalQuality.Evaluate(w!);
    }

    private static void OnlyCarrier(CarrierSpec c, bool clipped)
    {
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        if (!clipped)
            Assert.Contains("not clipped", Assert.Throws<ArgumentException>(() =>
                SignalComposer.Generate(new SignalSpec { Carriers = [c], ClipPaprDb = 8 })).Message);
        Assert.True(SignalComposer.HasOwnRecord(new SignalSpec { Carriers = [c] }));
    }

    [Fact]
    public void W1_Wlan_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = RoundTrip("WLAN;Std=ax;BW=80MHz;MCS=11;GI=0.8us;Symbols=4;Idle=16us;Oversample=2;Window=100ns;Off=0;P=0;Seed=1", CarrierStandard.Wlan);
        Assert.Equal((WlanFormat.He, 80e6, 11, 0.8e-6, 4, 2, 100e-9), (c.WlanFormat!.Value, c.BandwidthHz, c.Mcs!.Value, c.GuardIntervalS!.Value, c.Symbols!.Value, c.Oversample!.Value, c.WindowS!.Value));
        Assert.StartsWith("Wi-Fi 802.11ax 80 MHz MCS11 (", c.Label);
        RoundTrip("WLAN;BW=20MHz;Off=0;P=0;Seed=1", CarrierStandard.Wlan);
        Refuses(("WLAN;Std=ac;MCS=10", "MCS must be 0 to 9"), ("WLAN;Std=ad", "Std must be"), ("WLAN;BW=30MHz", "BW must be one of"),
                ("LTE;MCS=3", "MCS belongs to a WLAN carrier"), ("WLAN;Rs=1Msps", "RS belongs to an SC carrier"));
    }

    [Fact]
    public void W2_Wlan_ComposesAtItsRate_PacketPowerOne_ReadsItsOwnEvm()
    {
        var c = SignalSpec.ParseCarrier("WLAN;Std=ac;BW=40MHz;MCS=9;Symbols=6;Oversample=2;Off=0;P=0;Seed=2");
        OnlyCarrier(c, clipped: false);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var s = Wlan.Resolve(c);
        var rec = sig.Carriers.Single();
        Assert.Equal(80e6, rec.NativeRateHz);
        Assert.Equal(Wlan.RecordSamples(s), rec.Native.Length);
        Assert.Equal(Wlan.RecordSeconds(s), sig.RecordSeconds, 15);
        // Pavg is the packet's power, as a pulse's is its top's
        Assert.Equal(1.0, rec.Native.Take(Wlan.PacketSamples(s)).Average(v => v.Magnitude * v.Magnitude), 9);
        var q = Own(sig);
        Assert.True(q.ErrorPercent < 0.3, $"{q.ErrorPercent} %");
        Assert.Equal(100 * Math.Pow(10, Wlan.EvmLimitDb(s) / 20), q.LimitPercent, 9);
        Assert.NotNull(q.Points);
    }

    [Fact]
    public void G1_Gnss_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = RoundTrip("GNSS;Signal=L1CA;Sats=1@1.8kHz@12@0dB,5@-2kHz@300@-3dB;CN0=45;Record=20ms;Off=0;P=0;Seed=1", CarrierStandard.Gnss);
        Assert.Equal(2.046e6, c.BandwidthHz);
        Assert.Equal("GPS L1 C/A 2 SV 45 dB-Hz", c.Label);
        var s = Gnss.Resolve(c);
        Assert.Equal(new GnssSatellite(5, -2e3, 300, -3), s.Satellites[1]);
        RoundTrip("GNSS;Off=0;P=0;Seed=1", CarrierStandard.Gnss);
        Refuses(("GNSS;Sats=40", "PRN must be 1 to 32"), ("GNSS;Signal=L2", "Signal must be"), ("GNSS;Record=15ms", "20 ms frames"),
                ("LTE;Sats=1", "SATS belongs to a GNSS carrier"));
    }

    [Fact]
    public void G2_Gnss_ComposesAtItsRate_CorrelatesEachSatellite()
    {
        var c = SignalSpec.ParseCarrier("GNSS;Signal=E1;Sats=3@1kHz@100,11@-1.5kHz@2000@-2dB;CN0=48;Record=4ms;Off=0;P=0;Seed=3");
        OnlyCarrier(c, clipped: false);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var rec = sig.Carriers.Single();
        Assert.Equal(24.552e6, rec.NativeRateHz);
        Assert.Equal(4e-3, sig.RecordSeconds, 15);
        Assert.Equal(1.0, rec.Native.Average(v => v.Magnitude * v.Magnitude), 9);
        var q = Own(sig);
        Assert.Equal([3, 11], q.Gnss!.Select(g => g.Prn));
        Assert.Equal(100, q.Gnss[0].CodePhaseChips, 0.1);
        Assert.Equal(2000, q.Gnss[1].CodePhaseChips, 0.1);
        Assert.Equal(48, q.Gnss[0].Cn0DbHz, 2.0);
    }
}
