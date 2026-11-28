// The pulsed-CW carrier (brief ENV-PCW §1, gates 1 and 2).
//
//   P1  The netlist form parses, writes back to itself, survives the .cmod JSON, and labels as width / PRI; every
//       time unit means the same thing.
//   P2  One refusal per rule, each a sentence naming the field.
//   P3  The record: exactly 0 off, exactly 1 on the top, 10-90 % edges of Rise / Fall and a 50 %-50 % width of PW
//       to one sample, the analytic power duty, one PRI long, and a bandwidth taken from the edges.
//   P4  A linear-FM chirp: the instantaneous frequency is a line from -B/2 at the rising edge's 50 % point to +B/2 at
//       the falling edge's (reversed for Chirp=-B), |envelope| is the plain pulse's, and the bandwidth and rate grow to
//       hold the sweep. Chirp=0 writes nothing, keeps the cache key, and generates today's record bit for bit.
//   P5  With the radar options unset, the plain pulse and the chirp are the values stored here (worked from the
//       raised-cosine and the chirp's formula outside this code), to 1e-9 relative.

using System;
using System.Linq;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class PulsedCwTests
{
    private const string Text = "PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0";

    [Fact]
    public void P1_ParsesRoundTripsAndLabels()
    {
        var c = SignalSpec.ParseCarrier(Text);
        Assert.Equal(CarrierStandard.Pcw, c.Standard);
        Assert.Equal((100e-6, 50e-6, 0.5e-6, 0.5e-6, 10e-6),
                     (c.PriSeconds!.Value, c.PulseWidthSeconds!.Value, c.RiseSeconds!.Value, c.FallSeconds!.Value, c.DelaySeconds!.Value));
        Assert.Equal(Text, SignalSpec.FormatCarrier(c));
        Assert.Equal(c, SignalSpec.ParseCarrier(SignalSpec.FormatCarrier(c)));
        var spec = new SignalSpec { Carriers = [c] };
        Assert.Equal(c, SignalSpec.FromJson(spec.ToJson()).Carriers.Single());
        Assert.Equal("pCW 50 µs / 100 µs", c.Label);
        Assert.Equal("pCW 50 µs / 100 µs", spec.Summary);

        foreach (var pw in new[] { "50e-6", "5e-5", "0.05ms", "50000ns", "50µs" })
            Assert.Equal(50e-6, SignalSpec.ParseCarrier($"PCW;PRI=100us;PW={pw}").PulseWidthSeconds);

        // an unstated Delay stays unstated, and centres the pulse
        var centred = SignalSpec.ParseCarrier("PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us");
        Assert.Null(centred.DelaySeconds);
        Assert.DoesNotContain("Delay", SignalSpec.FormatCarrier(centred));
        var s = PulsedCw.Resolve(centred);
        Assert.Equal(s.Delay, s.Pri - s.End, 12);
    }

    [Theory]
    [InlineData("PCW;PRI=100us;PW=100us", "PW (100 µs) must be shorter than PRI")]
    [InlineData("PCW;PRI=100us;PW=10us;Rise=6us", "Rise (6 µs) is longer than half of PW")]
    [InlineData("PCW;PRI=100us;PW=10us;Fall=-1ns", "Fall must be a positive time")]
    [InlineData("PCW;PRI=100us;PW=50us;Delay=60us", "past the PRI")]
    [InlineData("PCW;PRI=100us;PW=50us;Delay=0", "Delay must be greater than 0")]
    [InlineData("PCW;PRI=100.5us;PW=50us", "not a whole number of samples")]
    [InlineData("PCW;PRI=100us;PW=50us;MeasureStart=0.9;MeasureEnd=0.1", "MeasureStart and MeasureEnd")]
    [InlineData("CW;PRI=100us", "PRI belongs to a PCW carrier")]
    [InlineData("PCW;PW=5 sec", "is not a time")]
    public void P2_RefusesWithTheField(string text, string fragment)
    {
        var ex = Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier(text));
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void P2_RefusesAPulseBesideAnotherCarrier_OrClipped()
    {
        var pcw = SignalSpec.ParseCarrier(Text);
        var ex = Assert.Throws<ArgumentException>(() => SignalComposer.Generate(new SignalSpec { Carriers = [pcw, new CarrierSpec { Standard = CarrierStandard.Cw }] }));
        Assert.Contains("only carrier", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SignalComposer.Generate(new SignalSpec { Carriers = [pcw], ClipPaprDb = 8 }));
    }

    [Fact]
    public void P3_TheRecordIsTheStatedPulse()
    {
        var c = SignalSpec.ParseCarrier(Text);
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c] });
        var rec = sig.Carriers.Single();
        var s = PulsedCw.Resolve(c);
        double fs = rec.NativeRateHz, dt = 1 / fs;
        Assert.Equal(30.72e6, fs);                                   // >= 10 samples in a 0.5 µs edge
        Assert.Equal(100e-6, sig.RecordSeconds);
        Assert.Equal((int)Math.Round(100e-6 * fs), rec.Native.Length);
        Assert.Equal(PulsedCw.BandwidthHz(s), rec.Spec.BandwidthHz);
        Assert.Equal(1.0, rec.RelativePower);

        var a = rec.Native.Select(v => v.Real).ToArray();
        Assert.All(rec.Native, v => Assert.Equal(0.0, v.Imaginary));
        for (int i = 0; i < a.Length; i++)
        {
            double t = i * dt;
            if (t <= s.Delay || t >= s.End) Assert.Equal(0.0, a[i]);
            else if (t >= s.TopStart && t <= s.TopEnd) Assert.Equal(1.0, a[i]);
        }
        double Cross(double level, bool up, int from = 0)
        {
            for (int i = from; i + 1 < a.Length; i++)
                if (up ? a[i] < level && a[i + 1] >= level : a[i] > level && a[i + 1] <= level)
                    return (i + (level - a[i]) / (a[i + 1] - a[i])) * dt;
            return double.NaN;
        }
        int mid = (int)(0.5 * (s.TopStart + s.TopEnd) * fs);
        Assert.Equal(s.Rise, Cross(0.9, true) - Cross(0.1, true), dt);
        Assert.Equal(s.Fall, Cross(0.1, false, mid) - Cross(0.9, false, mid), dt);
        Assert.Equal(s.Width, Cross(0.5, false, mid) - Cross(0.5, true), dt);
        Assert.Equal(s.PowerDuty, a.Average(x => x * x), 1e-6);
    }

    [Fact]
    public void P4_AChirpSweepsItsBandwidthOverTheWidth_AndZeroIsThePlainPulse()
    {
        var plain = SignalSpec.ParseCarrier(Text);
        foreach (double b in new[] { 20e6, -20e6 })
        {
            var c = SignalSpec.ParseCarrier(Text + (b > 0 ? ";Chirp=20MHz" : ";Chirp=-20MHz"));
            Assert.Equal(b, c.ChirpHz);
            Assert.Equal(c, SignalSpec.ParseCarrier(SignalSpec.FormatCarrier(c)));
            var s = PulsedCw.Validate(c);
            double rate = PulsedCw.NativeRateHz(s);
            Assert.True(rate >= 2 * c.BandwidthHz && c.BandwidthHz > b / 0.9 * Math.Sign(b), $"{rate} {c.BandwidthHz}");
            var x = PulsedCw.Generate(s, rate);
            var a = PulsedCw.Generate(PulsedCw.Resolve(plain), rate);
            Assert.Equal(a.Select(v => v.Real), x.Select(v => v.Magnitude), new Tol(1e-12));
            // the frequency from one sample to the next, at the 50 % points and the centre
            double F(double t)
            {
                int i = (int)Math.Round(t * rate);
                return (x[i + 1] * System.Numerics.Complex.Conjugate(x[i])).Phase * rate / (2 * Math.PI);
            }
            double t0 = s.Delay + s.RiseSpan / 2, t1 = t0 + s.Width;
            Assert.Equal(-b / 2, F(t0), 1e-3 * Math.Abs(b));
            Assert.Equal(0, F(s.Centre), 1e-3 * Math.Abs(b));
            Assert.Equal(b / 2, F(t1 - 1 / rate), 1e-3 * Math.Abs(b));
        }

        var zero = SignalSpec.ParseCarrier(Text + ";Chirp=0");
        Assert.Null(zero.ChirpHz);
        Assert.Equal(SignalSpec.FormatCarrier(plain), SignalSpec.FormatCarrier(zero));
        Assert.Equal(new SignalSpec { Carriers = [plain] }.CacheKey(), new SignalSpec { Carriers = [zero] }.CacheKey());
        var ps = PulsedCw.Resolve(plain);
        double r0 = PulsedCw.NativeRateHz(ps);
        var today = Enumerable.Range(0, (int)Math.Round(ps.Pri * r0)).Select(i => new System.Numerics.Complex(ps.At(i / r0), 0)).ToArray();
        Assert.Equal(today, PulsedCw.Generate(PulsedCw.Resolve(zero), r0));
        Assert.Equal(today, SignalComposer.Generate(new SignalSpec { Carriers = [zero] }).Carriers[0].Native);
    }

    [Fact]
    public void P5_TheRadarOptionsUnsetLeaveThePulseAndTheChirp()
    {
        static void Near(double expected, double actual) => Assert.Equal(expected, actual, 1e-9 * Math.Max(Math.Abs(expected), 1e-300));
        var s = PulsedCw.Check(PulsedCw.Resolve(SignalSpec.ParseCarrier(Text)));
        Assert.Null(s.Code);
        Assert.Null(s.Train);
        Assert.Equal(PulseFm.Linear, s.Fm);
        Assert.Equal(30.72e6, PulsedCw.NativeRateHz(s));
        Assert.Equal(100e-6, PulsedCw.RecordSeconds(s));
        var x = PulsedCw.Generate(s, 30.72e6);
        Assert.Equal(3072, x.Length);
        foreach (var (i, v) in new[] { (310, 0.028302788594545103), (315, 0.20582688139984), (320, 0.4873492017714499), (1000, 1.0),
                                       (1850, 0.8407292570149936), (1855, 0.5727636183813857), (1860, 0.27907405439733), (0, 0.0), (3000, 0.0) })
        {
            Near(v, x[i].Real);
            Assert.Equal(0.0, x[i].Imaginary);
        }

        var c = PulsedCw.Check(PulsedCw.Resolve(SignalSpec.ParseCarrier(Text + ";Chirp=20MHz")));
        Assert.Equal(61.44e6, PulsedCw.NativeRateHz(c));
        var y = PulsedCw.Generate(c, 61.44e6);
        Assert.Equal(6144, y.Length);
        foreach (var (i, re, im) in new[] { (310, 0.0, 0.0), (620, -0.01598483737740611, 0.023356643942257477),
                                            (640, 0.44324708877468055, 0.2025864328133547), (1600, -0.7950698395613465, -0.606517889447537),
                                            (3500, 0.4098813260044023, -0.9121388592713682), (3710, -0.44909041539961164, -0.3554939680747648) })
        {
            Near(re, y[i].Real);
            Near(im, y[i].Imaginary);
        }
    }

    private sealed class Tol(double eps) : System.Collections.Generic.IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= eps;
        public int GetHashCode(double v) => 0;
    }
}
