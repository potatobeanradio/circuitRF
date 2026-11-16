// The FSK carrier: continuous-phase binary FSK, with a Gaussian filter GFSK / GMSK.
//
//   F1  The netlist form parses, writes back to itself, survives the .cmod JSON and labels with its kind; refusals name
//       the field.
//   F2  PN9 and PN15 are maximal-length: every non-zero state once per period, so 2^(n-1) ones.
//   F3  The record: unit magnitude, one data period long, +-Dev on a rectangular bit, and the seam no worse than one
//       bit edge (the closing ramp moves the carrier by at most half a record bin). A Gaussian filter keeps the
//       frequency inside +-Dev and takes the step out of a bit edge.
//   F4  It is the source's only carrier and is not clipped; the envelope rate climbs from its native rate, since the
//       LTE / NR ladder does not divide a 511-bit record.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class FskTests
{
    private const string Text = "FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0";

    [Fact]
    public void F1_ParsesRoundTripsLabelsAndRefuses()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Equal((CarrierStandard.Fsk, 100e3, 45e3, 0.5, "PN9"), (c.Standard, c.BitRateBps!.Value, c.DeviationHz!.Value, c.GaussianBt!.Value, c.Data));
        Assert.Equal(290e3, c.BandwidthHz);                                // Carson: 2 (Dev + Rb)
        Assert.Equal(Text, SignalSpec.FormatCarrier(c));
        Assert.Equal(c, SignalSpec.FromJson(new SignalSpec { Carriers = [c] }.ToJson()).Carriers.Single());
        Assert.Equal("GFSK 100 kb/s ±45 kHz BT 0.5 PN9", c.Label);
        Assert.Equal("GMSK", Fsk.Resolve(SignalSpec.ParseCarrier("FSK;Rb=270.833kbps;Dev=67.70825kHz;BT=0.3")).Kind);
        Assert.Equal(32768, SignalSpec.ParseCarrier("FSK;Rb=32.768kbps").BitRateBps);
        Assert.Equal("FSK;Rb=32.768kbps;Off=0;P=0", SignalSpec.FormatCarrier(SignalSpec.ParseCarrier("FSK;Rb=32768")));

        foreach (var (text, fragment) in new[]
        {
            ("FSK;Rb=0", "Rb must be a positive bit rate"),
            ("FSK;Dev=-5kHz", "Dev must be a positive frequency deviation"),
            ("FSK;Data=10120", "Data must be PN9, PN15 or a pattern"),
            ("FSK;Rb=1kbps;Dev=500kHz;Data=PN15", "a record may hold"),
            ("LTE;Rb=100kbps", "RB belongs to an FSK, DBPSK or SC carrier"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier(text)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9, 5)]
    [InlineData(15, 14)]
    public void F2_PnSequencesAreMaximalLength(int order, int tap)
    {
        var b = Fsk.Prbs(order, tap);
        int n = b.Length, mask = n;
        Assert.Equal(1 << (order - 1), b.Count(x => x == 1));
        var seen = new bool[n + 1];
        for (int i = 0; i < n; i++)
        {
            int w = 0;
            for (int k = 0; k < order; k++) w = (w << 1) | b[(i + k) % n];
            Assert.False(seen[w & mask]);
            seen[w & mask] = true;
        }
        Assert.False(seen[0]);
    }

    [Fact]
    public void F3_TheRecordIsOnePeriodAtPlusMinusDev_WithASmoothSeam()
    {
        var s = Fsk.Resolve(SignalSpec.ParseCarrier("FSK;Rb=100kbps;Dev=45kHz;Data=PN9"));
        double rate = Fsk.NativeRateHz(s);
        Assert.Equal(3.2e6, rate);                                            // 32 samples per bit
        var (x, _) = Fsk.Generate(s, rate);
        Assert.Equal(511 * 32, x.Length);
        Assert.All(x, v => Assert.Equal(1, v.Magnitude, 12));
        double Freq(int i) => (x[(i + 1) % x.Length] * Complex.Conjugate(x[i])).Phase * rate / (2 * Math.PI);
        var bits = Fsk.DataBits("PN9");
        double ramp = 1 / (2 * s.RecordSeconds);                               // at most half a record bin: 97.8 Hz
        for (int k = 0; k < bits.Length; k++)
            Assert.InRange(Freq(k * 32 + 16), (bits[k] == 1 ? 45e3 : -45e3) - ramp, (bits[k] == 1 ? 45e3 : -45e3) + ramp);
        Assert.True(Math.Abs(Freq(x.Length - 1) - (bits[^1] == 1 ? 45e3 : -45e3)) <= ramp, "the seam is an ordinary sample");

        var g = Fsk.Resolve(SignalSpec.ParseCarrier("FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9"));
        var (_, f) = Fsk.Generate(g, rate);
        Assert.True(f.Max() <= 45e3 * (1 + 1e-9) && f.Min() >= -45e3 * (1 + 1e-9));
        double steepest = f.Zip(f.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Assert.True(steepest < 0.25 * 90e3, $"a Gaussian bit edge is no step ({steepest:0} Hz per sample)");
    }

    [Fact]
    public void F4_OnlyCarrier_NotClipped_RateFromItsOwn()
    {
        var c = SignalSpec.ParseCarrier("FSK;Rb=100kbps;Dev=45kHz;Data=PN9");
        Assert.Contains("only carrier", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c, new CarrierSpec()] })).Message);
        Assert.Contains("not clipped", Assert.Throws<ArgumentException>(() =>
            SignalComposer.Generate(new SignalSpec { Carriers = [c], ClipPaprDb = 8 })).Message);

        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        Assert.Equal(5.11e-3, sig.RecordSeconds, 15);
        Assert.Equal(1.0, sig.Carriers[0].Native.Average(v => v.Magnitude * v.Magnitude), 12);
        double r = SignalRate.AtLeast(10e6, [sig]);
        Assert.Equal(12.8e6, r);                                              // 3.2 MS/s x 4, not 15.36 from the ladder
        Assert.True(SignalRate.Divides(sig, sig.SuggestedCompositeRateHz()));
    }
}
