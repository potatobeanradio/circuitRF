// M-ary FSK, frequency pulses, bursts and the FSK-to-DPSK packet, with the FSK quality numbers.
//
//   M1  4FSK holds +-Dev/3 and +-Dev at the symbol centres, mapped 00 +1, 01 +3, 10 -1, 11 -3 (TIA-102.BAAA-A,
//       ETSI TS 102 361-1 cl. 10.2.1).
//   M2  The pulse narrows the spectrum: C4FM (TIA-102.BAAA-A) and 3RC CPM keep far less power outside the channel than
//       rectangular FSK of the same deviation.
//   M3  Every preset that is FSK alone is a constant envelope while on: PAPR 0 dB.
//   M4  Bluetooth EDR (Core v5.4 Vol 2 Part B cl. 6.7): the GFSK access code and header, a 5 us guard, then the payload
//       from the switch's function, starting on the right sample; ramps and off-time after it, 625 us in all.
//   M5  The deviation of a clean record reads within 0.1 %, and a 5 % high one reads 5 % high.
//   M6  With the new options unset the record is byte for byte what it was.
//   M7  Bursts lay out as the standard does (GSM normal burst, 3GPP TS 45.002 cl. 5.2.3); refusals name the field.

using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class FskMaryTests
{
    private static FskShape Preset(string name) => FskPresets.All.Single(p => p.Name == name).Shape;

    private static (Complex[] X, double[] F, double Rate) Gen(FskShape s)
    {
        double rate = Fsk.NativeRateHz(s);
        var (x, f) = Fsk.Generate(s, rate);
        return (x, f, rate);
    }

    private static double Freq(Complex[] x, int i, double rate) => (x[(i + 2) % x.Length] * Complex.Conjugate(x[i])).Phase * rate / (4 * Math.PI);

    [Fact]
    public void M1_FourLevelsAtTheSymbolCentres()
    {
        var s = new FskShape(9600, 1800, 0, "PN9") { Levels = 4 };
        Assert.Equal((4800.0, 0.25, "4FSK"), (s.SymbolRate, s.ModulationIndex, s.Kind));
        var (x, _, rate) = Gen(s);
        var bits = Fsk.RecordBits(s);
        Assert.Equal(1022, bits.Length);                                       // PN9 twice: 511 whole symbols
        var at = Fsk.SymbolCentres(s, rate);
        int[] level = [1, 3, -1, -3];
        double ramp = s.SymbolRate / 511;                                      // the closing ramp, under one record bin
        for (int j = 0; j < at.Length; j++)
            Assert.InRange(Freq(x, at[j], rate) - level[bits[2 * j] * 2 + bits[2 * j + 1]] * 600.0, -ramp, ramp);
    }

    private static double OutsideFraction(FskShape s, double halfHz)
    {
        var (x, _, rate) = Gen(s);
        var X = Fft.Forward(x);
        int n = X.Length;
        double all = 0, out_ = 0;
        for (int k = 0; k < n; k++)
        {
            double p = X[k].Magnitude * X[k].Magnitude, fk = (k <= n / 2 ? k : k - n) * rate / n;
            all += p;
            if (Math.Abs(fk) > halfHz) out_ += p;
        }
        return out_ / all;
    }

    [Fact]
    public void M2_ThePulseNarrowsTheSpectrum()
    {
        var rect = new FskShape(9600, 1800, 0, "PN9") { Levels = 4 };
        double r = OutsideFraction(rect, 6.25e3), c4fm = OutsideFraction(Preset("P25 Phase 1 C4FM"), 6.25e3);
        Assert.True(c4fm < r / 5, $"C4FM {c4fm:E2} outside the 12.5 kHz channel, rectangular {r:E2}");
        double one = OutsideFraction(new FskShape(100e3, 25e3, 0, "PN9"), 150e3);
        double rc3 = OutsideFraction(new FskShape(100e3, 25e3, 0, "PN9") { Pulse = FskPulse.Lrc, PulseLength = 3 }, 150e3);
        Assert.True(rc3 < one / 10, $"3RC {rc3:E2}, 1REC {one:E2}");
    }

    [Fact]
    public void M3_FskPresetsAreAConstantEnvelope()
    {
        foreach (var p in FskPresets.All.Where(p => p.Shape.Burst?.Then is null))
        {
            Fsk.Check(p.Shape);
            var (x, _, rate) = Gen(p.Shape);
            var l = Fsk.Layout(p.Shape, rate);
            var on = x.Skip(l.Ramp).Take(l.Fsk).Select(v => v.Magnitude * v.Magnitude).ToArray();
            double papr = 10 * Math.Log10(on.Max() / on.Average());
            Assert.True(papr < 1e-9 && Math.Abs(on.Average() - 1) < 1e-12, $"{p.Name}: PAPR {papr} dB");
        }
    }

    [Fact]
    public void M4_EdrSwitchesToTheDpskPayloadOnTime()
    {
        var s = Preset("EDR 2 Mb/s");
        double rate = Fsk.NativeRateHz(s);
        FskSwitch? got = null;
        // stand-in payload: a quarter-turn every symbol at half amplitude, so its start shows
        Complex[] StandIn(FskSwitch t, string data, double fs)
        {
            got = t;
            int sps = (int)Math.Round(fs / t.SymbolRate);
            return [.. Enumerable.Range(0, t.Symbols * sps).Select(i => Complex.FromPolarCoordinates(0.5, Math.PI / 2 * (i / sps)))];
        }
        var (x, _) = Fsk.Generate(s, rate, StandIn);
        Assert.Equal(("PI/4-DQPSK", 245), (got!.Modulation, got.Symbols));
        int us = (int)Math.Round(rate / 1e6);
        Assert.Equal(625 * us, x.Length);                                      // one 625 us slot
        int ramp = 2 * us, start = ramp + 126 * us + 5 * us;                   // ramp, access code + header, guard
        Assert.Equal(start, Fsk.Layout(s, rate).PayloadStart);
        Assert.All(x[ramp..start], v => Assert.Equal(1, v.Magnitude, 12));      // GFSK, then the unmodulated guard
        Assert.All(x[start..(start + 245 * us)], v => Assert.Equal(0.5, v.Magnitude, 12));
        Assert.All(x[(start + 245 * us + ramp)..], v => Assert.Equal(0, v.Magnitude));
        Assert.Throws<InvalidOperationException>(() => Fsk.Generate(s, rate));  // no payload function: refused, not silent
    }

    [Theory]
    [InlineData("BR")]
    [InlineData("P25 Phase 1 C4FM")]
    [InlineData("DMR")]
    [InlineData("Mode 1a 50 kb/s h 0.5")]
    [InlineData("4FSK 200 kb/s h 0.33")]
    public void M5_DeviationReadsWithinATenthOfAPercent(string name)
    {
        var s = Preset(name);
        var (x, _, rate) = Gen(s);
        var q = FskQuality.Measure(s, x, rate);
        Assert.True(Math.Abs(q.DeviationErrorPercent) < 0.1 && q.FskErrorRmsPercent < 0.1, $"{name}: {q}");
        var (hi, _, _) = Gen(s with { Deviation = 1.05 * s.Deviation });
        Assert.Equal(5, FskQuality.Measure(s, hi, rate).DeviationErrorPercent, 1);
    }

    private static string Hash(FskShape s)
    {
        var (x, f, _) = Gen(s);
        var d = x.SelectMany(v => new[] { v.Real, v.Imaginary }).Concat(f).ToArray();
        var b = new byte[d.Length * 8];
        Buffer.BlockCopy(d, 0, b, 0, b.Length);
        return Convert.ToHexString(SHA256.HashData(b))[..16];
    }

    [Fact]
    public void M6_UnsetOptionsLeaveTheRecordAsItWas()
    {
        // hashes of the binary generator before levels, pulses and bursts existed
        Assert.Equal("E63CD084D9031EC5", Hash(new FskShape(100e3, 45e3, 0, "PN9")));
        Assert.Equal("FF774D7BAE024F4C", Hash(new FskShape(100e3, 45e3, 0.5, "PN9")));
        Assert.Equal("1B8B665E2FD57D37", Hash(new FskShape(270833.333, 67708.33, 0.3, "1100101")));
    }

    [Fact]
    public void M7_BurstLayoutAndRefusals()
    {
        var gsm = Preset("Normal burst");
        Assert.Equal("GMSK", gsm.Kind);
        var bits = Fsk.RecordBits(gsm);
        Assert.Equal(148, bits.Length);                                        // 3 + 58 + 26 + 58 + 3
        Assert.Equal("00100101110000100010010111", string.Concat(bits.Skip(61).Take(26)));
        Assert.Equal(156.25 / gsm.BitRate, gsm.RecordSeconds, 12);
        Assert.Equal(1120, Fsk.RecordBits(Preset("POCSAG 1200 b/s")).Length);   // 576 preamble + 32 sync + 16 x 32
        Assert.Equal(1022, Fsk.RecordBits(Preset("R1 9.6 kb/s")).Length);       // Manchester: two chips a bit

        foreach (var (s, fragment) in new[]
        {
            (new FskShape(1e3, 1e3, 0, "PN9") { Levels = 3 }, "Levels must be 2, 4 or 8"),
            (new FskShape(1e3, 1e3, 0, "PN9") { Levels = 4, Mapping = [1, 3, -1, 2] }, "odd level"),
            (new FskShape(1e3, 1e3, 0, "PN9") { Pulse = FskPulse.RootRaisedCosine, RollOff = 0 }, "roll-off"),
            (new FskShape(1e3, 1e3, 0, "PN9") { Burst = new(RampSymbols: 4, GuardSymbols: 2) }, "guard must hold both ramps"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => Fsk.Check(s)).Message, StringComparison.Ordinal);
    }
}
