// The signal families wired through the MOD_Source: each one's netlist form, its record through SignalComposer, and its
// quality read back from the record the source plays (SignalQuality.Receive / Evaluate, as a probe's envelope is read).
//
//   I1  Carriers that existed before the families format and cache exactly as they did.
//   W*  Wi-Fi (WLAN).
//   G*  GNSS.
//   D*  DSSS: WCDMA, 802.11b, 802.15.4.
//   S*  The single carrier's pulses, rotation, differential and offset forms, frames and bursts.
//   F*  FSK's levels, pulses, Manchester and presets (bursts, the EDR switch to DPSK).
//   A*  Analog and navigation carriers.

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

    [Fact]
    public void D1_Dsss_ParseRoundTripLabelsAndRefusals()
    {
        var w = RoundTrip("WCDMA;Model=Tm1x64;Code=2;Off=0;P=0", CarrierStandard.Wcdma);
        Assert.Equal((5e6, "WCDMA TM1x64 code 2"), (w.BandwidthHz, w.Label));
        var b = RoundTrip("WLAN11B;Rb=5.5Mbps;PSDU=200;Off=0;P=0", CarrierStandard.Wlan11b);
        Assert.Equal((22e6, "802.11b 5.5 Mb/s 200 B"), (b.BandwidthHz, b.Label));
        var z = RoundTrip("OQPSK154;PSDU=20;Off=0;P=0", CarrierStandard.Oqpsk154);
        Assert.Equal((5e6, "802.15.4 O-QPSK 20 B"), (z.BandwidthHz, z.Label));
        RoundTrip("WCDMA;Off=0;P=0", CarrierStandard.Wcdma);
        Refuses(("WCDMA;Model=TM9", "Model must be one of"), ("WCDMA;Code=9", "0 to 7"), ("LTE;Model=Tm2", "MODEL belongs to a WCDMA carrier"),
                ("WLAN11B;Rb=3Mbps", "Rb must be 1, 2, 5.5 or 11"), ("OQPSK154;PSDU=200", "1 to 127 bytes"),
                ("LTE;PSDU=10", "PSDU belongs to a WLAN11B or OQPSK154 carrier"), ("OQPSK154;Rb=1Mbps", "RB belongs to an FSK"));
    }

    [Fact]
    public void D2_Wcdma_ComposesAtItsRate_ReadsEvmPcdeAndCodePower_AndClips()
    {
        var c = SignalSpec.ParseCarrier("WCDMA;Model=Tm1x16;Off=0;P=0");
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var rec = sig.Carriers.Single();
        Assert.Equal((30.72e6, 307200, 10e-3), (rec.NativeRateHz, rec.Native.Length, sig.RecordSeconds));
        Assert.Equal(1.0, rec.Native.Average(v => v.Magnitude * v.Magnitude), 9);
        var q = Own(sig);
        Assert.True(q.ErrorPercent < 0.01 && q.Wcdma!.PeakCdeDb < -50, $"EVM {q.ErrorPercent} %, PCDE {q.Wcdma!.PeakCdeDb} dB");
        Assert.Equal(-10, q.Wcdma.Channels.Single(x => x.Channel.Kind == WcdmaChannelKind.Cpich).PowerDb, 0.2);
        Assert.Equal(17.5, q.LimitPercent);
        Assert.NotNull(SignalComposer.WcdmaOf(new SignalSpec { Carriers = [c], ClipPaprDb = 7 }));   // Clip applies, as to SC
    }

    [Fact]
    public void D3_Wlan11bAnd154_ComposeAtTheirRates_ReadTheirEvm()
    {
        var b = SignalSpec.ParseCarrier("WLAN11B;Rb=11Mbps;PSDU=100;Off=0;P=0");
        OnlyCarrier(b, clipped: false);
        var sb = SignalComposer.Generate(new SignalSpec { Carriers = [b] });
        Assert.Equal(88e6, sb.Carriers.Single().NativeRateHz);
        var qb = Own(sb);
        Assert.True(qb.ErrorPercent < 0.01 && qb.MaskMarginDb > 0, $"EVM {qb.ErrorPercent} %, mask margin {qb.MaskMarginDb} dB");

        var z = SignalSpec.ParseCarrier("OQPSK154;PSDU=20;Off=0;P=0");
        OnlyCarrier(z, clipped: false);
        var sz = SignalComposer.Generate(new SignalSpec { Carriers = [z] });
        Assert.Equal((16e6, new Oqpsk154Shape(20).RecordSeconds), (sz.Carriers.Single().NativeRateHz, sz.RecordSeconds));
        Assert.All(sz.Carriers.Single().Native, v => Assert.Equal(1.0, v.Magnitude, 9));
        Assert.True(Own(sz).ErrorPercent < 0.01, $"EVM {Own(sz).ErrorPercent} %");
    }

    private const string Edge = "SC;Rs=1Msps;Mod=8PSK;Preamble=26;Pulse=LinearGMSK;Rotation=67.5;FrameData=122;Guard=8.25;Ramp=4;Off=0;P=0";

    [Fact]
    public void S1_Sc_NewFieldsParseRoundTripAndRefuse()
    {
        var c = RoundTrip(Edge, CarrierStandard.Sc);
        var s = ScQam.Resolve(c);
        Assert.Equal((ScPulse.LinearGmsk, 67.5, 122, 8.25, 4.0), (s.Pulse, s.RotationDeg, s.FrameData, s.Guard, s.Ramp));
        Assert.EndsWith(" LinearGMSK", c.Label);
        Assert.True(ScQam.Resolve(RoundTrip("SC;Rs=1Msps;Mod=QPSK;RollOff=0.4;Differential=1;Off=0;P=0", CarrierStandard.Sc)).Differential);
        Assert.True(ScQam.Resolve(RoundTrip("SC;Rs=1Msps;Pulse=HalfSine;OffsetQ=1;Off=0;P=0", CarrierStandard.Sc)).Offset);
        Assert.Equal(0.5, ScQam.Resolve(RoundTrip("SC;Pulse=Gaussian;BT=0.5;Off=0;P=0", CarrierStandard.Sc)).Bt);
        Refuses(("SC;Mod=16QAM;Differential=1", "Differential takes BPSK, QPSK or 8PSK"), ("SC;Pulse=Sinc", "unknown pulse"),
                ("SC;Guard=1;Ramp=1", "needs a Guard of at least twice"), ("FSK;Rotation=90", "ROTATION belongs to an SC carrier"),
                ("SC;Differential=2", "is 1 or 0"));
    }

    [Fact]
    public void S2_ScBurst_IsUnitPowerOverItsFrames_AndReadsItsOwnRecord()
    {
        var c = SignalSpec.ParseCarrier(Edge);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var rec = sig.Carriers.Single();
        var w = ScQam.Window(ScQam.Resolve(c));
        Assert.Equal(1.0, rec.Native.Where((_, i) => w[i] == 1).Average(v => v.Magnitude * v.Magnitude), 9);   // Pavg is the on-power
        Assert.True(rec.Native.Average(v => v.Magnitude * v.Magnitude) < 0.99);
        var q = Own(sig);
        Assert.True(q.ErrorPercent < 1e-6, $"{q.ErrorPercent} %");
        Assert.True(Own(SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier("SC;Mod=BPSK;Rotation=90;Off=0;P=0")] })).ErrorPercent < 1e-6);
    }

    [Fact]
    public void F1_Fsk_NewFieldsParseRoundTripAndRefuse()
    {
        var c = RoundTrip("FSK;Rb=9.6kbps;Dev=1.944kHz;Levels=4;Mapping=1,3,-1,-3;Pulse=RRC;RollOff=0.2;Off=0;P=0", CarrierStandard.Fsk);
        var s = Fsk.Resolve(c);
        Assert.Equal((4, FskPulse.RootRaisedCosine, 0.2, 4800.0), (s.Levels, s.Pulse, s.RollOff, s.SymbolRate));
        Assert.Contains("(4.8ksps)", c.Label);
        var p = RoundTrip("FSK;Preset=Bluetooth EDR 2 Mb/s;Off=0;P=0", CarrierStandard.Fsk);
        Assert.Equal("PI/4-DQPSK", Fsk.Resolve(p).Burst!.Then!.Modulation);
        Assert.StartsWith("Bluetooth EDR 2 Mb/s: ", p.Label);
        Assert.Equal("FSK;Preset=DECT;Off=0;P=0", SignalSpec.FormatCarrier(SignalSpec.ParseCarrier("FSK;Preset=dect")));
        Assert.True(Fsk.Resolve(RoundTrip("FSK;Rb=19.2kbps;Manchester=1;Off=0;P=0", CarrierStandard.Fsk)).Manchester);
        Assert.Equal(3, Fsk.Resolve(RoundTrip("FSK;Pulse=LRC;PulseLength=3;Off=0;P=0", CarrierStandard.Fsk)).PulseLength);
        Refuses(("FSK;Preset=Morse", "unknown preset"), ("FSK;Levels=3", "Levels must be 2, 4 or 8"), ("FSK;Pulse=Sinc", "unknown pulse"),
                ("SC;Levels=4", "LEVELS belongs to an FSK carrier"), ("LTE;Pulse=RRC", "PULSE belongs to an FSK or SC carrier"));
    }

    [Fact]
    public void F2_FskEdr_ComposesWithItsDpskPayload_AndReadsBothParts()
    {
        var c = SignalSpec.ParseCarrier("FSK;Preset=Bluetooth EDR 3 Mb/s;Off=0;P=0");
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var s = Fsk.Resolve(c);
        var rec = sig.Carriers.Single();
        Assert.Equal(Fsk.NativeRateHz(s), rec.NativeRateHz);
        Assert.Equal(625e-6, sig.RecordSeconds, 12);                       // one slot
        var l = Fsk.Layout(s, rec.NativeRateHz);
        // on-power 1: the GFSK part unit magnitude, the DPSK payload unit mean power; off after the burst
        Assert.Equal(1.0, rec.Native.Skip(l.Ramp).Take(l.Fsk).Average(v => v.Magnitude), 9);
        Assert.Equal(1.0, rec.Native.Skip(l.PayloadStart).Take(l.Payload).Average(v => v.Magnitude * v.Magnitude), 6);
        Assert.Equal(0, rec.Native[^1].Magnitude);
        var q = Own(sig);
        Assert.True(q.ErrorPercent < 0.1, $"FSK error {q.ErrorPercent} %");
        Assert.True(q.Switched!.RmsPercent < 0.1 && q.Switched.Symbols > 200, $"payload EVM {q.Switched.RmsPercent} % over {q.Switched.Symbols}");
    }

    [Fact]
    public void A1_Analog_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = RoundTrip("ANALOG;Kind=FM;Tones=1kHz@0@0;Dev=75kHz;Off=0;P=0;Seed=1", CarrierStandard.Analog);
        Assert.Equal((152e3, "FM 152 kHz"), (c.BandwidthHz, c.Label));
        var v = RoundTrip("ANALOG;Kind=VOR;Bearing=45;Ident=ABC;Record=200ms;Off=0;P=0;Seed=1", CarrierStandard.Analog);
        Assert.Equal((AnalogKind.Vor, 45.0, "ABC"), (Analog.Resolve(v).Kind, Analog.Resolve(v).BearingDeg, v.Ident));
        RoundTrip("ANALOG;Kind=USB;Tones=700Hz@0@0,1.9kHz@-3@90;Off=0;P=0;Seed=1", CarrierStandard.Analog);
        Refuses(("ANALOG;Kind=QAM", "Kind must be"), ("ANALOG;Kind=AM;Depth=1.5", "Depth must be above 0"),
                ("LTE;Depth=0.3", "DEPTH belongs to an ANALOG carrier"), ("ANALOG;Kind=FM;Ident=X", "Ident belongs to a VOR"));
    }

    [Fact]
    public void A2_Analog_ComposesAtItsRate_AndReadsItsModulationBack()
    {
        var c = SignalSpec.ParseCarrier("ANALOG;Kind=AM;Depth=0.5;Off=0;P=0;Seed=1");
        OnlyCarrier(c, clipped: false);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var s = Analog.Validate(Analog.Resolve(c));
        Assert.Equal((Analog.NativeRateHz(s), 0.1), (sig.Carriers.Single().NativeRateHz, sig.RecordSeconds));
        Assert.Equal(1.0, sig.Carriers.Single().Native.Average(v => v.Magnitude * v.Magnitude), 9);
        var q = Own(sig).Analog!;
        Assert.Equal((0.5, 1000.0), (Math.Round(q.Depth, 6), q.MessageHz));
        var vor = Own(SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier("ANALOG;Kind=VOR;Bearing=45;Off=0;P=0;Seed=1")] })).Analog!;
        Assert.Equal(45, vor.BearingDeg, 0.01);
    }

    [Fact]
    public void T1_Tones_ParseRoundTripLabelsAndRefusals_CwPhaseUnchanged()
    {
        var c = RoundTrip("TONES;N=100;Spacing=100kHz;Phase=Random;Notch=0/1MHz;Off=0;P=0;Seed=1", CarrierStandard.Tones);
        Assert.Equal((10e6, "100 tones × 100 kHz, notch 1 MHz"), (c.BandwidthHz, c.Label));
        var n = RoundTrip("TONES;N=0;Notch=0/0.36MHz;NoiseBW=36MHz;Record=1ms;Off=0;P=0;Seed=1", CarrierStandard.Tones);
        Assert.Equal((36e6, "noise 36 MHz, notch 360 kHz"), (n.BandwidthHz, n.Label));
        Assert.Equal(90, RoundTrip("CW;Phase=90;Off=0.5MHz;P=0;Seed=1", CarrierStandard.Cw).PhaseDeg);
        Refuses(("TONES;Phase=Odd", "Phase must be Zero"), ("TONES;Notch=1MHz", "is centre/width"),
                ("LTE;Spacing=1MHz", "SPACING belongs to a TONES carrier"), ("TONES;N=3;Spacing=1MHz;Powers=0,1", "2 powers for 3 tones"),
                ("PCW;Phase=Random", "PHASE belongs to a CW carrier"));
    }

    [Fact]
    public void T2_Tones_ComposeAtTheirRate_ReadNpr_AndClipFillsTheNotch()
    {
        var c = SignalSpec.ParseCarrier("TONES;N=0;Notch=0/0.1MHz;NoiseBW=1MHz;Record=1ms;Off=0;P=0;Seed=1");
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var s = SignalSpec.ResolveTones(c);
        Assert.Equal((Multitone.NativeRateHz(s), 1e-3), (sig.Carriers.Single().NativeRateHz, sig.RecordSeconds));
        double clean = Own(sig).NprDb, clipped = Own(SignalComposer.Generate(new SignalSpec { Carriers = [c], ClipPaprDb = 5 })).NprDb;
        Assert.True(clean > 100 && clipped < 60, $"NPR clean {clean:0.#} dB, clipped to 5 dB {clipped:0.#} dB");
        var plain = SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier("TONES;N=2;Off=0;P=0;Seed=1")] }).Carriers.Single();
        Assert.Null(SignalQuality.Receive(plain, 2e-6, plain.Native, plain.NativeRateHz, 0, 0, out string? why));
        Assert.Contains("Notch=", why);
    }
}
