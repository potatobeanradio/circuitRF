// Tones and noise (Multitone): N-tone, notched multitone, band-limited Gaussian noise, CW + AWGN, NPR.
//
//   T1  Ten tones 1 MHz apart with powers 0, -1 .. -9 dB: every tone on its bin at (k - 4.5) MHz with its power
//       to 1e-9 relative, nothing anywhere else; the record has unit mean power.
//   T2  The clean records' notches are empty: the 100-tone NPR preset reads an NPR above 100 dB (the transform's
//       floor), and so does the 36 MHz noise NPR preset.
//   T3  NPR readout of a known mix: the 36 MHz noise NPR record plus tones on every notch bin at a density 30 dB
//       under the noise's reads 30 dB within 0.2 dB.
//   T4  Gaussian noise statistics: AWGN 20 MHz has E|x|^4 / (E|x|^2)^2 = 2 (a complex Gaussian's) within 0.05, and its
//       PAPR at 1e-4 is 10 log10(ln 1e4) = 9.64 dB within 0.4 dB (13 samples above it in 131,072). OFDM's 10-12 dB
//       is above that because it is read at 0.01 % over a far longer record, and clipped tails differ; noise is
//       the Gaussian reference itself.
//   T5  Phases: 100 equal tones in phase peak at 20 dB (10 log N) at 8x; Schroeder's phases hold the peak under
//       4 dB, also for a 10 dB power tilt across the tones; the two-tone preset's PAPR is 3.01 dB.
//   T6  CW + AWGN: the tone over the noise's total is the stated 20 dB, to the noise draw's own scatter.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class MultitoneTests
{
    private static MultitoneShape Preset(string name) => MultitonePresets.All.Single(p => p.Name == name).Shape;

    private static double PeakDb(Complex[] x, int up = 8)
        => SignalMeasure.PaprDb(Fft.ResamplePeriodic(x, up * x.Length), 0);

    [Fact]
    public void T1_TonesSitOnTheirBins_AtTheirPowers()
    {
        var s = new MultitoneShape { Tones = 10, SpacingHz = 1e6, Phase = TonePhase.Newman, TonePowersDb = [.. Enumerable.Range(0, 10).Select(k => -1.0 * k)] };
        var x = Multitone.Generate(s);
        Assert.Equal(1, OfdmGenerator.MeanPower(x), 12);
        double rec = Multitone.RecordSeconds(s), fs = Multitone.NativeRateHz(s);
        Assert.Equal(x.Length / fs, rec, 15);
        var X = Fft.Forward(x);
        var p = X.Select(v => v.Magnitude * v.Magnitude / ((double)x.Length * x.Length)).ToArray();   // Parseval: sums to 1
        double total = Enumerable.Range(0, 10).Sum(k => Math.Pow(10, -k / 10.0));
        var used = new bool[x.Length];
        for (int k = 0; k < 10; k++)
        {
            int bin = (int)((Math.Round((k - 4.5) * 1e6 * rec) + x.Length) % x.Length);
            used[bin] = true;
            Assert.Equal(Math.Pow(10, -k / 10.0) / total, p[bin], 1e-9 * p[bin]);
        }
        Assert.True(p.Where((_, i) => !used[i]).Max() < 1e-25);
    }

    [Fact]
    public void T2_TheCleanNotchIsEmpty()
    {
        var mt = Preset("100-tone NPR");
        var x = Multitone.Generate(mt);
        Assert.Equal(90, Multitone.Powers(mt).Count(p => p > 0));
        Assert.True(Multitone.NprDb(x, Multitone.NativeRateHz(mt), 0, 10e6, 0, 1e6) > 100);

        var ns = Preset("Noise NPR, 36 MHz");
        var y = Multitone.Generate(ns);
        Assert.True(Multitone.NprDb(y, Multitone.NativeRateHz(ns), 0, 36e6, 0, 360e3) > 100);
    }

    [Fact]
    public void T3_NprReadsBackAKnownMix()
    {
        var ns = Preset("Noise NPR, 36 MHz");
        var x = Multitone.Generate(ns);
        double fs = Multitone.NativeRateHz(ns), rec = Multitone.RecordSeconds(ns);
        // the noise's nominal density per 1 ms bin: unit power over the band's 36,001 bins, less the 359 the notch empties
        double perBin = 1.0 / (36001 - 359);
        // a tone on every bin of the notch (|f| < 180 kHz, 359 bins), 30 dB under that, random phases
        var fill = new MultitoneShape { Tones = 359, SpacingHz = 1 / rec, Phase = TonePhase.Random, Seed = 7, RecordSeconds = rec };
        var z = Fft.ResamplePeriodic(Multitone.Generate(fill), x.Length);
        double a = Math.Sqrt(359 * perBin * 1e-3);
        var y = x.Zip(z, (u, v) => u + a * v).ToArray();
        Assert.Equal(30, Multitone.NprDb(y, fs, 0, 36e6, 0, 360e3), 0.2);
    }

    [Fact]
    public void T4_AwgnIsGaussian()
    {
        var x = Multitone.Generate(Preset("AWGN 20 MHz"));
        Assert.Equal(131072, x.Length);
        double m2 = x.Average(v => v.Magnitude * v.Magnitude), m4 = x.Average(v => Math.Pow(v.Magnitude, 4));
        Assert.Equal(2, m4 / (m2 * m2), 0.05);
        Assert.Equal(10 * Math.Log10(Math.Log(1e4)), SignalMeasure.PaprDb(x, 1e-4), 0.4);
    }

    [Fact]
    public void T5_SchroederPhasesHoldThePeakDown()
    {
        var zero = new MultitoneShape { Tones = 100, SpacingHz = 100e3, Phase = TonePhase.Zero };
        Assert.Equal(20, PeakDb(Multitone.Generate(zero)), 1e-6);
        Assert.True(PeakDb(Multitone.Generate(zero with { Phase = TonePhase.Schroeder })) < 4);
        var tilt = zero with { Phase = TonePhase.Schroeder, TonePowersDb = [.. Enumerable.Range(0, 100).Select(k => -0.1 * k)] };
        Assert.True(PeakDb(Multitone.Generate(tilt)) < 4);
        Assert.Equal(10 * Math.Log10(2), PeakDb(Multitone.Generate(Preset("Two-tone"))), 1e-9);
    }

    [Fact]
    public void T6_CwPlusAwgn_HasItsCarrierToNoise()
    {
        var s = Preset("CW + AWGN");
        var X = Fft.Forward(Multitone.Generate(s));
        double tone = X[0].Magnitude * X[0].Magnitude, all = X.Sum(v => v.Magnitude * v.Magnitude);
        // bin 0 holds the tone plus one noise bin (1 in 20,001 of the noise): within 0.1 dB of 20 dB
        Assert.Equal(20, 10 * Math.Log10(tone / (all - tone)), 0.1);
    }
}
