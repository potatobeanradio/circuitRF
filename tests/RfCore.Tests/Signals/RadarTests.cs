// Radar waveforms on the pulsed-CW generator.
//
//   R1  Barker: the code values, every aperiodic sidelobe 0 or 1, and Barker 13's matched filter PSL
//       20 log10(1/13) = -22.3 dB, ISL -11.5 dB, bare and as a generated 13 µs pulse; chip smoothing keeps |envelope|.
//   R2  Frank, P1, P2, P3, P4 for a small length against the published formulas worked by hand.
//   R3  Zadoff-Chu: constant amplitude, zero periodic autocorrelation; m-sequences of degree 2-16 are maximal length
//       (two-valued periodic autocorrelation n, -1).
//   R4  NLFM: the generated pulse's instantaneous frequency is the tangent / sine law.
//   R5  FMCW: the ramp's slope is B / Ramp, and the phase is continuous everywhere, the record's wrap included.
//   R6  Trains: staggered, jittered and burst-and-dwell timing; stepped frequencies.
//   R7  DFS: FCC Type 0 and ETSI signal 5 timing; every preset passes the checks.
//   R8  DME: the pair spacing and the half-amplitude width.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class RadarTests
{
    private static PulseShape Preset(string name) => RadarPresets.All.Single(p => p.Name == name).Pulse!;

    private static double Wrap(double p) => Math.IEEERemainder(p, 2 * Math.PI);

    private static void SamePhases(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(0, Wrap(expected[i] - actual[i]), 1e-12);
    }

    /// <summary>Times (s) the magnitude rises through half its peak.</summary>
    private static List<double> Rising(Complex[] x, double rate, double level = 0.5)
    {
        var a = x.Select(v => v.Magnitude).ToArray();
        var t = new List<double>();
        for (int i = 0; i + 1 < a.Length; i++)
            if (a[i] < level && a[i + 1] >= level) t.Add((i + (level - a[i]) / (a[i + 1] - a[i])) / rate);
        return t;
    }

    [Fact]
    public void R1_Barker()
    {
        Assert.Equal([1, 1, 1, 1, 1, -1, -1, 1, 1, -1, 1, -1, 1], RadarCodes.Barker(13));
        foreach (int n in new[] { 2, 3, 4, 5, 7, 11, 13 })
        {
            var b = RadarCodes.Barker(n);
            for (int k = 1; k < n; k++) Assert.InRange(Math.Abs(Enumerable.Range(0, n - k).Sum(i => b[i] * b[i + k])), 0, 1);
        }
        Assert.Throws<FormatException>(() => RadarCodes.Barker(6));

        var bare = PulseCompression.Measure(RadarCodes.Phases(RadarCode.Barker, 13));
        Assert.Equal(20 * Math.Log10(1 / 13.0), bare.PslDb, 1e-9);          // -22.28 dB
        Assert.Equal(10 * Math.Log10(12 / 169.0), bare.IslDb, 1e-9);        // -11.49 dB
        Assert.Equal(2, bare.NullToNullSeconds, 1e-12);                       // ±1 chip

        var s = PulsedCw.Check(Preset("Barker 13"));
        double rate = PulsedCw.NativeRateHz(s);
        var pulse = PulsedCw.OnePulse(s, rate);
        var m = PulseCompression.Measure(pulse, rate);
        Assert.Equal(-22.3, m.PslDb, 0.3);
        Assert.Equal(2e-6, m.NullToNullSeconds, 2 / rate + 0.05e-6);         // ±1 chip of 1 µs
        Assert.InRange(m.MainlobeSeconds, 0.5e-6, 0.7e-6);                    // 2 (1 - 1/√2) chip for rectangular chips

        // smoothing the chip steps changes the phase only
        var smooth = PulsedCw.Generate(s with { ChipSmoothing = 0.5 }, rate);
        var plain = PulsedCw.Generate(s, rate);
        for (int i = 0; i < plain.Length; i++) Assert.Equal(plain[i].Magnitude, smooth[i].Magnitude, 1e-12);
        double chip = s.Width / 13;
        for (int c = 0; c < 13; c++)
        {
            int i = (int)Math.Round((s.Lead + (c + 0.5) * chip) * rate);
            Assert.Equal(0, Wrap(smooth[i].Phase - s.Code![c]), 1e-9);
        }
    }

    [Fact]
    public void R2_MatrixAndFrequencyDerivedCodes()
    {
        double p = Math.PI;
        // Frank M = 3: 2π (i-1)(j-1) / 3
        SamePhases([0, 0, 0, 0, 2 * p / 3, 4 * p / 3, 0, 4 * p / 3, 8 * p / 3], RadarCodes.Frank(3));
        // P1 M = 2: -(π/2)[2 - (2j-1)][(j-1)2 + (i-1)]
        SamePhases([0, -p / 2, p, 3 * p / 2], RadarCodes.P1(2));
        // P2 M = 2: [(π/2)(1/2) - (π/2)(i-1)](3 - 2j), a palindrome
        SamePhases([p / 4, -p / 4, -p / 4, p / 4], RadarCodes.P2(2));
        var p2 = RadarCodes.P2(4);
        SamePhases([.. p2.Reverse()], p2);
        Assert.Throws<FormatException>(() => RadarCodes.P2(3));
        // P3, P4 N = 4: π (i-1)² / 4, and minus π (i-1)
        SamePhases([0, p / 4, p, 9 * p / 4], RadarCodes.P3(4));
        SamePhases([0, -3 * p / 4, -p, -3 * p / 4], RadarCodes.P4(4));
    }

    [Fact]
    public void R3_ZadoffChuAndMSequences()
    {
        foreach (var (n, u) in new[] { (63, 1), (64, 5), (139, 7) })
        {
            var z = RadarCodes.ZadoffChu(n, u).Select(ph => Complex.FromPolarCoordinates(1, ph)).ToArray();
            for (int k = 1; k < n; k++)
            {
                Complex r = 0;
                for (int i = 0; i < n; i++) r += z[(i + k) % n] * Complex.Conjugate(z[i]);
                Assert.True(r.Magnitude < 1e-9 * n, $"N={n} u={u} lag {k}: {r.Magnitude}");
            }
        }
        Assert.Throws<FormatException>(() => RadarCodes.ZadoffChu(64, 2));

        for (int m = 2; m <= 16; m++)
        {
            var bits = RadarCodes.Mls(m);
            int n = (1 << m) - 1;
            Assert.Equal(n, bits.Length);
            var x = Fft.Forward(bits.Select(b => new Complex(1 - 2 * b, 0)).ToArray());
            var r = Fft.Inverse(x.Select(v => new Complex(v.Magnitude * v.Magnitude, 0)).ToArray());
            Assert.Equal(n, r[0].Real, 1e-6 * n);
            for (int k = 1; k < n; k++) Assert.True(Math.Abs(r[k].Real + 1) < 1e-6 * n, $"degree {m} lag {k}: {r[k].Real}");
        }
    }

    [Fact]
    public void R4_NlfmFollowsItsLaw()
    {
        foreach (var name in new[] { "Tangent NLFM 10 MHz", "Sine NLFM 10 MHz" })
        {
            var s = PulsedCw.Check(Preset(name));
            double rate = PulsedCw.NativeRateHz(s), b = s.ChirpHz, beta = s.FmShape;
            var x = PulsedCw.Generate(s, rate);
            foreach (double tau in new[] { -0.45, -0.3, -0.1, 0, 0.2, 0.35, 0.45 })
            {
                double t = s.Centre + tau * s.Width;
                int i = (int)Math.Floor(t * rate);
                double f = (x[i + 1] * Complex.Conjugate(x[i])).Phase * rate / (2 * Math.PI);
                double tm = (i + 0.5) / rate, tt = (tm - s.Centre) / s.Width;
                double law = s.Fm == PulseFm.Tangent
                    ? b / 2 * Math.Tan(2 * beta * tt) / Math.Tan(beta)
                    : b * (tt - beta * Math.Sin(2 * Math.PI * tt) / (2 * Math.PI));
                Assert.Equal(law, f, 1e-4 * b);
            }
            Assert.Equal(b / 2, s.FrequencyAt(s.Centre + s.Width / 2), 1e-9 * b);
            Assert.Equal(-b / 2, s.FrequencyAt(s.Centre - s.Width / 2), 1e-9 * b);
            Assert.Equal(0, s.FrequencyAt(s.Centre), 1e-9 * b);
        }
        // the tangent law lingers at the centre: its slope there is B β / tan β / PW < B / PW
        var tan = Preset("Tangent NLFM 10 MHz");
        double dt = 1e-9;
        Assert.True((tan.FrequencyAt(tan.Centre + dt) - tan.FrequencyAt(tan.Centre - dt)) / (2 * dt) < tan.ChirpHz / tan.Width);
    }

    [Fact]
    public void R5_FmcwSlopeAndContinuity()
    {
        foreach (var name in new[] { "FMCW sawtooth 250 MHz", "FMCW triangle 250 MHz" })
        {
            var r = RadarPresets.All.Single(p => p.Name == name).Ramp!;
            double rate = r.NativeRateHz(), b = r.BandwidthHz;
            var x = r.Generate(rate);
            Assert.Equal((int)Math.Round(r.Period * rate), x.Length);
            Assert.All(x, v => Assert.Equal(1, v.Magnitude, 1e-12));
            double F(int i) => (x[(i + 1) % x.Length] * Complex.Conjugate(x[i])).Phase * rate / (2 * Math.PI);
            int a = (int)(0.2 * r.Ramp * rate), c = (int)(0.8 * r.Ramp * rate);
            Assert.Equal(b / r.Ramp, (F(c) - F(a)) / ((c - a) / rate), 1e-6 * b / r.Ramp);
            // continuous: no sample-to-sample frequency beyond the sweep's edge, the wrap included
            for (int i = 0; i < x.Length; i++) Assert.InRange(Math.Abs(F(i)), 0, b / 2 * (1 + 1e-6));
            Assert.Equal(-b / 2, F(x.Length - 1), 2 * b / (r.Triangle ? r.Ramp : r.Idle) / rate);
        }
        Assert.Throws<FormatException>(() => new Fmcw(1e6, 10e-6, 1e-6, Triangle: true).Check());
    }

    [Fact]
    public void R6_TrainTiming()
    {
        var st = PulsedCw.Check(Preset("Stagger 100/125/150 µs"));
        double rate = PulsedCw.NativeRateHz(st);
        var x = PulsedCw.Generate(st, rate);
        Assert.Equal(375e-6, PulsedCw.RecordSeconds(st), 1e-15);
        Assert.Equal((int)Math.Round(375e-6 * rate), x.Length);
        var up = Rising(x, rate);
        Assert.Equal(3, up.Count);
        Assert.Equal(100e-6, up[1] - up[0], 1e-12);
        Assert.Equal(125e-6, up[2] - up[1], 1e-12);
        Assert.Equal(150e-6, up[0] + 375e-6 - up[2], 1e-12);

        var ji = PulsedCw.Check(Preset("Jitter ±5 %"));
        var (starts, _, record) = PulsedCw.Layout(ji);
        Assert.Equal(16, starts.Length);
        var gaps = starts.Zip(starts.Skip(1).Append(record), (p, q) => q - p).ToArray();
        Assert.All(gaps, g => Assert.InRange(g, 95e-6 - 1e-8, 105e-6 + 1e-8));      // to one sample
        Assert.True(gaps.Distinct().Count() > 8);
        Assert.Equal(starts, PulsedCw.Layout(ji).Starts);                                // the seed fixes it
        Assert.NotEqual(starts, PulsedCw.Layout(ji with { Train = ji.Train! with { Seed = 2 } }).Starts);

        var bu = PulsedCw.Check(Preset("Burst 8 + 1 ms dwell"));
        Assert.Equal(1.8e-3, PulsedCw.RecordSeconds(bu), 1e-15);
        Assert.Equal(8, Rising(PulsedCw.Generate(bu, PulsedCw.NativeRateHz(bu)), PulsedCw.NativeRateHz(bu)).Count);

        // stepped: pulse k at (k - 3.5) MHz, read at its centre
        var sf = PulsedCw.Check(Preset("Stepped frequency 8 x 1 MHz"));
        rate = PulsedCw.NativeRateHz(sf);
        x = PulsedCw.Generate(sf, rate);
        for (int k = 0; k < 8; k++)
        {
            int i = (int)Math.Round((k * sf.Pri + sf.Centre) * rate);
            Assert.Equal((k - 3.5) * 1e6, (x[i + 1] * Complex.Conjugate(x[i])).Phase * rate / (2 * Math.PI), 1e-3);
        }
    }

    [Fact]
    public void R7_DfsPatterns()
    {
        var t0 = PulsedCw.Check(Preset("Type 0"));
        double rate = PulsedCw.NativeRateHz(t0);
        var x = PulsedCw.Generate(t0, rate);
        var up = Rising(x, rate);
        Assert.Equal(18, up.Count);
        Assert.All(up.Zip(up.Skip(1), (p, q) => q - p), g => Assert.Equal(1428e-6, g, 1 / 30.72e6));
        Assert.Equal(1e-6, t0.Width);

        var e5 = PulsedCw.Check(Preset("Signal 5"));
        rate = PulsedCw.NativeRateHz(e5);
        up = Rising(PulsedCw.Generate(e5, rate), rate);
        Assert.Equal(30, up.Count);                                                       // 10 per PRF x 3 PRFs
        double[] pri = [1 / 300.0, 1 / 330.0, 1 / 360.0];
        for (int i = 0; i + 1 < up.Count; i++) Assert.Equal(pri[i % 3], up[i + 1] - up[i], 1 / 30.72e6);

        Assert.Equal(5e6, Preset("Signal 4").ChirpHz);                                    // ±2.5 MHz
        foreach (var p in RadarPresets.All)
        {
            if (p.Pulse is { } s) { PulsedCw.Check(s); Assert.True(PulsedCw.RecordSeconds(s) > 0, p.Name); }
            else p.Ramp!.NativeRateHz();
        }
    }

    [Fact]
    public void R8_DmePairs()
    {
        foreach (var (name, spacing) in new[] { ("DME X", 12e-6), ("DME Y interrogation", 36e-6), ("DME Y reply", 30e-6) })
        {
            var s = PulsedCw.Check(Preset(name));
            double rate = PulsedCw.NativeRateHz(s);
            var x = PulsedCw.Generate(s, rate);
            var a = x.Select(v => v.Magnitude).ToArray();
            var up = Rising(x, rate);
            Assert.Equal(2, up.Count);
            Assert.Equal(spacing, up[1] - up[0], 1e-9);                                   // half-amplitude leading edges
            double down = 0;
            for (int i = (int)(up[0] * rate) + 1; i + 1 < a.Length; i++)
                if (a[i] > 0.5 && a[i + 1] <= 0.5) { down = (i + (a[i] - 0.5) / (a[i] - a[i + 1])) / rate; break; }
            Assert.Equal(3.5e-6, down - up[0], 1e-8);                                     // 3.5 µs at half amplitude
            double rise = Rising(x, rate, 0.9)[0] - Rising(x, rate, 0.1)[0];
            Assert.InRange(rise, 2.4e-6, 3e-6);                                           // <= 3 µs; 2.51 µs nominal
        }
    }
}
