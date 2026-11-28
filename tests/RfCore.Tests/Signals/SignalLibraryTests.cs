// Modulated-signal library (envelope analysis, brief B1/B2).
//
//   S1  FFT: radix-2 and Bluestein lengths round-trip and agree with a direct DFT.
//   S2  Periodic resampling is exact for a band-limited record (up and back down).
//   S3  Numerology tables: LTE 20 MHz = 100 RB / 2048 / 30.72 MS/s; NR 20 MHz @30 kHz = 51 RB / 1024;
//       NR 100 MHz @30 kHz = 273 RB / 4096; a 0.5 ms slot holds exactly 0.5 ms of samples.
//   S4  Unclipped full-load OFDM has the natural PAPR (10–12.5 dB at 1e-4) and unit mean power.
//   S5  Shaped LTE/NR 20 MHz: ACLR of the ideal signal far below any amplifier's (< -60 dBc), in-band PSD flat.
//   S6  The PAPR target is met as a CEILING on the PEAK at 8x oversampling (what a DAC and the amplifier see): at
//       or under it within 0.05 dB, for LTE and NR at 7 / 8 / 9 dB and NR 100 at 9 dB, and the 0.01 % PAPR at or under
//       it too, also at the Designer's display rate. At the native rate the peaks fall between samples: a clip there
//       read 7.9 dB natively and 9.2 dB at 4x; a clip at 4x let NR 100 read 9.17 dB at 8x.
//   S7  Two carriers 400 MHz apart: per-carrier records kept separate, composite power = 1, each carrier at
//       its offset in the composite spectrum, equal shares at equal PowerDb.
//   S8  The cache returns the same signal for the same spec and a different one for a different seed.
//   S9  Inline carrier syntax round-trips.
//   S10 A carrier's frequencies mean the same in any unit: 20MHz, 20000kHz and 20e6 generate the identical signal.
//   S11 N tones (the N-tone preset's CW cards): two equal tones 1 MHz apart hold all the power in two equal bins at
//       +-0.5 MHz and peak 3.01 dB over the average; a CW card's Phase= round-trips, starts its tone there, and is
//       absent from a plain CW card's text.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class SignalLibraryTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(1536)]
    [InlineData(61440)]
    public void S1_Fft_MatchesDftAndRoundTrips(int n)
    {
        var rng = new Random(n);
        var x = Enumerable.Range(0, n).Select(_ => new Complex(rng.NextDouble() - .5, rng.NextDouble() - .5)).ToArray();
        var X = Fft.Forward(x);
        foreach (int k in new[] { 0, 1, n / 3, n - 1 })
        {
            Complex d = 0;
            for (int i = 0; i < n; i++) d += x[i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((long)k * i % n) / n);
            Assert.True((X[k] - d).Magnitude < 1e-8 * Math.Sqrt(n), $"bin {k}");
        }
        var y = Fft.Inverse(X);
        Assert.True(x.Zip(y, (a, b) => (a - b).Magnitude).Max() < 1e-10);
    }

    [Fact]
    public void S2_PeriodicResampling_IsExactForBandLimited()
    {
        int n = 960;
        var x = Enumerable.Range(0, n).Select(i => Complex.FromPolarCoordinates(1, 2 * Math.PI * 7 * i / n) + 0.3 * Complex.FromPolarCoordinates(1, -2 * Math.PI * 31 * i / n)).ToArray();
        var up = Fft.ResamplePeriodic(x, 4 * n);
        for (int i = 0; i < 4 * n; i += 37)
        {
            Complex e = Complex.FromPolarCoordinates(1, 2 * Math.PI * 7 * i / (4.0 * n)) + 0.3 * Complex.FromPolarCoordinates(1, -2 * Math.PI * 31 * i / (4.0 * n));
            Assert.True((up[i] - e).Magnitude < 1e-10);
        }
        var back = Fft.ResamplePeriodic(up, n);
        Assert.True(x.Zip(back, (a, b) => (a - b).Magnitude).Max() < 1e-10);
    }

    [Fact]
    public void S3_Numerology()
    {
        var lte = OfdmGenerator.Numerology(new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6 });
        Assert.Equal((100, 1200, 2048), (lte.NRb, lte.NSc, lte.Nfft));
        Assert.Equal(30.72e6, lte.SampleRateHz, 3);
        Assert.Equal(15360, lte.SamplesPerHalfMs);
        var nr = OfdmGenerator.Numerology(new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3 });
        Assert.Equal((51, 1024), (nr.NRb, nr.Nfft));
        Assert.Equal(15360, nr.SamplesPerHalfMs);
        Assert.Equal(18.36e6, nr.OccupiedBwHz, 3);
        var nr100 = OfdmGenerator.Numerology(new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 100e6, ScsHz = 30e3 });
        Assert.Equal((273, 4096), (nr100.NRb, nr100.Nfft));
        Assert.Equal(61440, nr100.SamplesPerHalfMs);
        Assert.Throws<ArgumentException>(() => OfdmGenerator.Numerology(new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 25e6 }));
    }

    [Theory]
    [InlineData(CarrierStandard.Lte, 30e3)]
    [InlineData(CarrierStandard.Nr, 30e3)]
    public void S4_Unclipped_NaturalPapr(CarrierStandard std, double scs)
    {
        var c = new CarrierSpec { Standard = std, BandwidthHz = 20e6, ScsHz = scs };
        var x = OfdmGenerator.Generate(c, 2, 0.2, null);
        Assert.Equal(1.0, OfdmGenerator.MeanPower(x), 6);
        var up = Fft.ResamplePeriodic(x, 4 * x.Length);
        double papr = SignalMeasure.PaprDb(up, 1e-4);
        Assert.InRange(papr, 9.5, 12.5);
    }

    [Theory]
    [InlineData(CarrierStandard.Lte)]
    [InlineData(CarrierStandard.Nr)]
    public void S5_ShapedAclr_IsClean_AndInBandFlat(CarrierStandard std)
    {
        var spec = new SignalSpec { Carriers = { }, RecordSeconds = 1e-3 };
        spec.Carriers.Clear();
        spec.Carriers.Add(new CarrierSpec { Standard = std, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam64 });
        var sig = SignalComposer.Generate(spec);
        var x = sig.CarrierAt(0, 122.88e6);
        var a = SignalMeasure.Aclr(x, 122.88e6, spec.Carriers[0]);
        Assert.True(a.WorstAcpr1 < -60, $"ACPR1 {a.WorstAcpr1:F1} dBc");
        Assert.True(a.WorstAcpr2 < -60, $"ACPR2 {a.WorstAcpr2:F1} dBc");
        var (f, p) = SignalMeasure.Welch(x, 122.88e6, 4096);
        var inband = f.Select((v, i) => (v, p[i])).Where(t => Math.Abs(t.v) < 8e6).Select(t => 10 * Math.Log10(t.Item2)).ToArray();
        Assert.True(inband.Max() - inband.Min() < 3.0, $"in-band ripple {inband.Max() - inband.Min():F2} dB");
    }

    [Theory]
    [InlineData(CarrierStandard.Lte, 8.0, 20e6)]
    [InlineData(CarrierStandard.Nr, 7.0, 20e6)]
    [InlineData(CarrierStandard.Nr, 8.0, 20e6)]
    [InlineData(CarrierStandard.Nr, 9.0, 20e6)]
    [InlineData(CarrierStandard.Nr, 9.0, 100e6)]
    public void S6_Clipping_HitsTheTargetPeakAt8x(CarrierStandard std, double target, double bw)
    {
        var spec = new SignalSpec { RecordSeconds = 1e-3, ClipPaprDb = target };
        spec.Carriers[0] = new CarrierSpec { Standard = std, BandwidthHz = bw, ScsHz = 30e3 };
        var sig = SignalComposer.Generate(spec);
        var c = sig.Carriers[0];
        var x4 = Fft.ResamplePeriodic(c.Native, c.Native.Length * OfdmGenerator.PaprOversample);
        // the target is a ceiling: the 8x peak at or under it (within 0.05 dB), and the 0.01 % PAPR with it
        double peak = SignalMeasure.PaprDb(x4, 0.0), p4 = SignalMeasure.PaprDb(x4, 1e-4);
        Assert.InRange(peak, target - 0.05, target + 1e-3);
        Assert.True(p4 <= target + 1e-3, $"0.01 % PAPR {p4:0.000} dB over the {target} dB target");
        // the preview's own view: the composite at the Designer's rate (4x native for NR 20, 8x for NR 100)
        double rate = sig.SuggestedCompositeRateHz();
        var xd = sig.Composite(rate);
        Assert.InRange(SignalMeasure.PaprDb(xd, 0.0), target - 0.15, target + 0.01);
        Assert.True(SignalMeasure.PaprDb(xd, 1e-4) <= target + 0.01);
    }

    [Fact]
    public void S7_TwoCarriers400MHz_SeparateAndComposite()
    {
        var spec = new SignalSpec
        {
            RecordSeconds = 0.5e-3,
            Carriers =
            {
                new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, OffsetHz = -200e6, Seed = 1 },
                new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6, OffsetHz = 200e6, Seed = 2 },
            },
        };
        spec.Carriers.RemoveAt(0);                     // the default single carrier
        var sig = SignalComposer.Generate(spec);
        Assert.Equal(2, sig.Carriers.Count);
        Assert.Equal(420e6, sig.InstantaneousBandwidthHz, 3);
        Assert.Equal(0.5, sig.Carriers[0].RelativePower, 9);
        double fs = sig.SuggestedCompositeRateHz();
        Assert.True(fs >= 2 * 250e6, $"composite rate {fs / 1e6} MS/s");
        var y = sig.Composite(fs);
        Assert.Equal(1.0, OfdmGenerator.MeanPower(y), 2);
        var (f, p) = SignalMeasure.Welch(y, fs, 8192);
        double pl = SignalMeasure.BandPower(f, p, -200e6, 18e6), pu = SignalMeasure.BandPower(f, p, 200e6, 18e6);
        double pmid = SignalMeasure.BandPower(f, p, 0, 18e6);
        Assert.InRange(10 * Math.Log10(pl / pu), -0.5, 0.5);
        Assert.True(10 * Math.Log10(pmid / pl) < -60);
        var c0 = sig.CarrierAt(0, 122.88e6);
        Assert.Equal(61440, c0.Length);
        Assert.Equal(0.5, OfdmGenerator.MeanPower(c0), 2);
    }

    [Fact]
    public void S8_Cache_SharesBySpec()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crf_sig_" + Guid.NewGuid().ToString("N"));
        var old = SignalCache.Directory;
        try
        {
            SignalCache.Directory = dir;
            SignalCache.ClearMemory();
            var a = new SignalSpec { RecordSeconds = 0.5e-3 };
            var s1 = SignalCache.Get(a);
            var s2 = SignalCache.Get(a with { });
            Assert.Same(s1, s2);
            SignalCache.ClearMemory();
            var s3 = SignalCache.Get(a);                                   // from disk
            Assert.Equal(s1.Carriers[0].Native[123], s3.Carriers[0].Native[123]);
            var b = new SignalSpec { RecordSeconds = 0.5e-3, Carriers = new() { new CarrierSpec { Seed = 7 } } };
            Assert.NotEqual(a.CacheKey(), b.CacheKey());
        }
        finally { SignalCache.Directory = old; SignalCache.ClearMemory(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void S9_InlineCarrierSyntax_RoundTrips()
    {
        var c = SignalSpec.ParseCarrier("NR;BW=20e6;SCS=30e3;Mod=256QAM;Off=-200e6;P=-3;Seed=5");
        Assert.Equal(CarrierStandard.Nr, c.Standard);
        Assert.Equal(QamOrder.Qam256, c.Modulation);
        Assert.Equal(-200e6, c.OffsetHz);
        Assert.Equal(c, SignalSpec.ParseCarrier(SignalSpec.FormatCarrier(c)));
        // Frequencies take a unit, and the Designer writes them with one: the same doubles either way.
        Assert.Equal(c, SignalSpec.ParseCarrier("NR;BW=20MHz;SCS=30kHz;Mod=256QAM;Off=-200MHz;P=-3;Seed=5"));
        Assert.Equal("NR;BW=20MHz;SCS=30kHz;Mod=256QAM;Off=-200MHz;P=-3;Seed=5", SignalSpec.FormatCarrier(c));
        Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier("NR;BW=20mhz"));
        Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier("WIFI;BW=20e6"));
    }

    [Fact]
    public void S10_CarrierUnits_GiveTheSameSignal()
    {
        // The same NR 20 MHz carrier in three spellings. Equal specs share one cache key, so a simulation reuses one
        // generation; generating each anyway shows the record itself is identical, sample for sample.
        string[] spellings =
        [
            "NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1",
            "NR;BW=20000kHz;SCS=0.03MHz;Mod=64QAM;Off=0Hz;P=0;Seed=1",
            "NR;BW=20e6;SCS=30e3;Mod=64QAM;Off=0;P=0;Seed=1",
        ];
        var specs = spellings.Select(t => new SignalSpec { RecordSeconds = 0.5e-3, Carriers = new() { SignalSpec.ParseCarrier(t) } }).ToArray();
        Assert.All(specs, sp => Assert.Equal(specs[0].CacheKey(), sp.CacheKey()));

        var reference = SignalComposer.Generate(specs[0]).Carriers[0].Native;
        foreach (var sp in specs.Skip(1))
            Assert.Equal(reference, SignalComposer.Generate(sp).Carriers[0].Native);
    }

    [Fact]
    public void S11_NTones_SitAtTheirOffsets_AtEqualPower_TwoTonePaprIs3dB()
    {
        var sig = SignalComposer.Generate(new SignalSpec
        {
            Carriers = [new CarrierSpec { Standard = CarrierStandard.Cw, OffsetHz = -0.5e6 },
                        new CarrierSpec { Standard = CarrierStandard.Cw, OffsetHz = 0.5e6 }],
            RecordSeconds = 1e-3,
        });
        double fs = 7.68e6;
        var x = sig.Composite(fs);
        var X = Fft.Forward(x);
        var p = X.Select(v => v.Magnitude * v.Magnitude).ToArray();
        double total = p.Sum(), df = fs / x.Length;
        int lo = (int)Math.Round(-0.5e6 / df) + x.Length, hi = (int)Math.Round(0.5e6 / df);
        Assert.Equal(0.5, p[lo] / total, 9);
        Assert.Equal(0.5, p[hi] / total, 9);
        Assert.Equal(10 * Math.Log10(2), SignalMeasure.PaprDb(x, 0), 6);

        var c = SignalSpec.ParseCarrier("CW;Phase=60;Off=1MHz;P=0");
        Assert.Equal(60, c.PhaseDeg);
        Assert.Equal("CW;Phase=60;Off=1MHz;P=0;Seed=1", SignalSpec.FormatCarrier(c));
        Assert.DoesNotContain("Phase", SignalSpec.FormatCarrier(SignalSpec.ParseCarrier("CW;Off=0;P=0")));
        var one = SignalComposer.Generate(new SignalSpec { Carriers = [c] }).Carriers[0].Native[0];
        Assert.Equal(60, one.Phase * 180 / Math.PI, 9);
    }
}
