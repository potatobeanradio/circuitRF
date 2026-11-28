// The GNSS ranging signals: GPS L1 C/A (IS-GPS-200N), GPS L5 (IS-GPS-705J), Galileo E1 (Galileo OS SIS ICD Issue 2.1)
// and BeiDou B1I (BDS-SIS-ICD-B1I-3.0), several satellites at once, and the correlation check that measures them.
//
//   G1  Chip rates and code periods (1 ms C/A, L5 and B1I, 4 ms E1); a code repeats every period within a data bit.
//   G2  The codes against the ICDs: C/A's first 10 chips in octal (IS-GPS-200N Table 3-Ia, PRN 1-19), L5's first 13
//       I5 and Q5 chips from the initial XB states (IS-GPS-705J Table 3-Ia); B1I's length and Gold-like correlation
//       (its ICD lists no chips); E1's stand-in codes are fixed, 4092 chips, data and pilot different.
//   G3  The spectrum: L1 C/A's main lobe holds sinc²'s share; E1 splits into two lobes about ±1.023 MHz with nulls at
//       0 and ±2.046 MHz, band by band within 0.5 dB of CBOC(6,1,1/11).
//   G4  A clean signal: each PRN found at its code phase with the correlation peak its share of the amplitude; a PRN
//       not in view shows no peak.
//   G5  With noise at a set C/N0, the estimate is within 1 dB for every signal.
//   G6  Refusals name the field.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class GnssTests
{
    [Fact]
    public void G1_ChipRatesAndCodePeriods()
    {
        foreach (var (sig, rate, chips, ms) in new[]
        {
            (GnssSignal.GpsL1Ca, 1.023e6, 1023, 1), (GnssSignal.GpsL5, 10.23e6, 10230, 1),
            (GnssSignal.GalileoE1, 1.023e6, 4092, 4), (GnssSignal.BeidouB1I, 2.046e6, 2046, 1),
        })
        {
            var b = Gnss.Band(sig);
            Assert.Equal((rate, chips, ms), (b.ChipRateHz, b.CodeChips, b.CodeMs));
            Assert.Equal(ms * 1e-3, chips / rate, 15);
            Assert.Equal(chips, Gnss.CodeBits(sig, 1).Length);
        }
        Assert.Equal(1575.42e6, Gnss.Band(GnssSignal.GpsL1Ca).CarrierHz);
        Assert.Equal(1176.45e6, Gnss.Band(GnssSignal.GpsL5).CarrierHz);
        Assert.Equal(1561.098e6, Gnss.Band(GnssSignal.BeidouB1I).CarrierHz);

        var s = new GnssShape(GnssSignal.GpsL1Ca, [new(1)]);
        var x = Gnss.Generate(s);
        int len = s.Band.CodeSamples;
        Assert.Equal(20 * len, x.Length);
        for (int n = 0; n < 19 * len; n++) Assert.Equal(x[n], x[n + len]);   // one data bit: the code repeats every 1 ms
        Assert.Equal(1.0, x.Average(v => v.Magnitude * v.Magnitude), 12);
    }

    [Fact]
    public void G2_CodesAgainstTheIcds()
    {
        int[] caOctal = [1440, 1620, 1710, 1744, 1133, 1455, 1131, 1454, 1626, 1504, 1642, 1750, 1764, 1772, 1775, 1776, 1156, 1467, 1633];
        for (int prn = 1; prn <= caOctal.Length; prn++)
        {
            int first10 = Gnss.CodeBits(GnssSignal.GpsL1Ca, prn).Take(10).Aggregate(0, (a, c) => 2 * a + c);
            Assert.Equal(caOctal[prn - 1].ToString(), Convert.ToString(first10, 8));
        }

        // initial XB code state, the rightmost bit first out; XA starts all 1s, so the code's first 13 chips are its complement
        (int Prn, string I5, string Q5)[] xb =
        [
            (1, "0101011100100", "1001011001100"), (2, "1100000110101", "0100011110110"), (3, "0100000001000", "1111000100011"),
            (4, "1011000100110", "0011101101010"), (5, "1110111010111", "0011110110010"), (10, "0111111011110", "0010010000110"),
            (14, "0100000100111", "1010000111111"), (37, "0011010010000", "0010011010001"),
        ];
        foreach (var (prn, i5, q5) in xb)
            for (int ch = 0; ch < 2; ch++)
            {
                string want = new([.. (ch == 0 ? i5 : q5).Reverse().Select(c => c == '1' ? '0' : '1')]);
                string got = string.Concat(Gnss.CodeBits(GnssSignal.GpsL5, prn, ch).Take(13));
                Assert.Equal(want, got);
            }

        // B1I: the ICD gives taps, not chips; its truncated Gold codes correlate as Gold codes do
        double Corr(byte[] a, byte[] b, int lag) => a.Select((v, k) => (1 - 2 * v) * (1 - 2 * b[(k + lag) % b.Length])).Sum();
        var bds = new[] { 1, 6, 19, 37, 38, 63 }.Select(p => Gnss.CodeBits(GnssSignal.BeidouB1I, p)).ToArray();
        Assert.All(bds, c => Assert.Equal(2046, c.Length));
        Assert.Equal(2046, Corr(bds[0], bds[0], 0));
        for (int i = 0; i < bds.Length; i++)
            for (int j = 0; j < bds.Length; j++)
                for (int lag = i == j ? 1 : 0; lag < 2046; lag += 37)
                    Assert.True(Math.Abs(Corr(bds[i], bds[j], lag)) < 0.1 * 2046, $"B1I {i},{j} lag {lag}");

        var e1b = Gnss.CodeBits(GnssSignal.GalileoE1, 7, 0);
        Assert.Equal(e1b, Gnss.CodeBits(GnssSignal.GalileoE1, 7, 0));
        Assert.NotEqual(e1b, Gnss.CodeBits(GnssSignal.GalileoE1, 7, 1));
        Assert.Equal(4092, e1b.Length);
    }

    /// <summary>The share of a record's power in [f1, f2] Hz, measured and from the expected PSD.</summary>
    private static (double Measured, double Expected) BandShare(GnssShape s, Complex[] x, double f1, double f2)
    {
        var X = Fft.Forward(x);
        double rate = Gnss.NativeRateHz(s), total = X.Sum(v => v.Magnitude * v.Magnitude), inBand = 0;
        for (int i = 0; i < X.Length; i++)
        {
            double f = (i <= X.Length / 2 ? i : i - X.Length) * rate / X.Length;
            if (f >= f1 && f <= f2) inBand += X[i].Magnitude * X[i].Magnitude;
        }
        double expected = 0, df = 100;
        for (double f = f1 + df / 2; f < f2; f += df) expected += Gnss.Psd(s.Signal, f) * df;
        return (inBand / total, expected);
    }

    [Fact]
    public void G3_SpectrumIsSincSquaredOrCboc()
    {
        var l1 = new GnssShape(GnssSignal.GpsL1Ca, [new(4)]);
        var (m, e) = BandShare(l1, Gnss.Generate(l1), -1.023e6, 1.023e6);
        Assert.Equal(0.903, e, 3);                                      // sinc²'s main lobe
        Assert.Equal(10 * Math.Log10(e), 10 * Math.Log10(m), 0.2);

        var e1 = new GnssShape(GnssSignal.GalileoE1, [new(11)]);
        var x = Gnss.Generate(e1);
        double Db(double f) { var (mm, ee) = BandShare(e1, x, f - 1e5, f + 1e5); Assert.Equal(10 * Math.Log10(ee), 10 * Math.Log10(mm), 0.5); return 10 * Math.Log10(mm); }
        double lo = Db(-1.023e6), hi = Db(1.023e6), dc = Db(0), nullLo = Db(-2.046e6), nullHi = Db(2.046e6);
        Assert.True(Math.Min(lo, hi) - Math.Max(dc, Math.Max(nullLo, nullHi)) > 15, $"peaks {lo:0.0}/{hi:0.0} dB, nulls {dc:0.0}/{nullLo:0.0}/{nullHi:0.0} dB");
        // the lobes sit either side of the 1.023 MHz subcarrier's lines; sine BOC(1,1)'s PSD, 4 Tc sin⁴(x/2) / x² with
        // x = π f Tc, peaks at x = 2.33 (0.74 MHz), 1.1 dB above its value at 1.023 MHz
        double fMax = Enumerable.Range(0, 3000).MaxBy(i => Gnss.Psd(GnssSignal.GalileoE1, i * 1e3)) * 1e3;
        Assert.InRange(fMax, 0.72e6, 0.78e6);
        Assert.Equal(1.1, 10 * Math.Log10(Gnss.Psd(GnssSignal.GalileoE1, fMax) / Gnss.Psd(GnssSignal.GalileoE1, 1.023e6)), 1);
    }

    [Theory]
    [InlineData(GnssSignal.GpsL1Ca, 7)]
    [InlineData(GnssSignal.GpsL5, 24)]
    [InlineData(GnssSignal.GalileoE1, 19)]
    [InlineData(GnssSignal.BeidouB1I, 30)]
    public void G4_CleanSignalPeaksAtItsCodePhase(GnssSignal sig, int prn)
    {
        var lone = new GnssShape(sig, [new(prn, DopplerHz: 1234, CodePhaseChips: 100.5)]);
        var c = Gnss.Correlate(lone, Gnss.Generate(lone), prn);
        Assert.Equal(100.5, c.CodePhaseChips);
        Assert.Equal(1.0, c.Peak, 2);
        Assert.True(c.PeakToFloorDb > 25, $"{c.PeakToFloorDb:0.0} dB");
    }

    [Fact]
    public void G4_SeveralSatellites_EachFoundAtItsShare()
    {
        var s = new GnssShape(GnssSignal.GpsL1Ca,
            [new(3, 2000, 0, 0), new(11, -1500, 300, -3), new(22, 4000, 777.25, -6)]);
        var x = Gnss.Generate(s);
        double total = s.Satellites.Sum(v => Math.Pow(10, v.PowerDb / 10));
        foreach (var sat in s.Satellites)
        {
            var c = Gnss.Correlate(s, x, sat.Prn);
            Assert.Equal(sat.CodePhaseChips, c.CodePhaseChips);
            Assert.Equal(Math.Sqrt(Math.Pow(10, sat.PowerDb / 10) / total), c.Peak, 1);
            Assert.True(c.PeakToFloorDb > 20, $"PRN {sat.Prn}: {c.PeakToFloorDb:0.0} dB");
        }
        Assert.True(Gnss.Correlate(s, x, 5).PeakToFloorDb < 10);       // not in view
    }

    [Theory]
    [InlineData(GnssSignal.GpsL1Ca, 45, 20)]
    [InlineData(GnssSignal.GpsL5, 45, 20)]
    [InlineData(GnssSignal.GalileoE1, 42, 40)]
    [InlineData(GnssSignal.BeidouB1I, 44, 20)]
    public void G5_Cn0EstimateWithin1Db(GnssSignal sig, double cn0, int ms)
    {
        int p1 = 2, p2 = 9;                                             // B1I: PRN 2 is a GEO (D2), PRN 9 a MEO/IGSO (D1)
        var s = new GnssShape(sig, [new(p1, 1800, 12), new(p2, -2600, 345, -3)], Cn0DbHz: cn0, RecordMs: ms, Seed: 7);
        var x = Gnss.Generate(s);
        Assert.Equal(cn0, Gnss.Correlate(s, x, p1).Cn0DbHz, 1.0);
        Assert.Equal(cn0 - 3, Gnss.Correlate(s, x, p2).Cn0DbHz, 1.0);
        Assert.Equal(12, Gnss.Correlate(s, x, p1).CodePhaseChips);
    }

    [Fact]
    public void G6_Refusals()
    {
        foreach (var (s, fragment) in new[]
        {
            (new GnssShape(GnssSignal.GpsL1Ca, []), "needs at least one satellite"),
            (new GnssShape(GnssSignal.GpsL1Ca, [new(33)]), "PRN must be 1 to 32"),
            (new GnssShape(GnssSignal.GpsL1Ca, [new(1), new(1, 100)]), "its own PRN"),
            (new GnssShape(GnssSignal.GpsL1Ca, [new(1, 6e4)]), "Doppler must be within"),
            (new GnssShape(GnssSignal.GpsL1Ca, [new(1)], RecordMs: 30), "whole number of 20 ms frames"),
            (new GnssShape(GnssSignal.GalileoE1, [new(1)], RecordMs: 10), "whole number of 4 ms frames"),
            (new GnssShape(GnssSignal.GpsL5, [new(1)], RecordMs: 400), "over the"),
            (new GnssShape(GnssSignal.GpsL1Ca, [new(1)], Cn0DbHz: -1), "C/N0 must be"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => Gnss.Validate(s)).Message, StringComparison.Ordinal);
    }
}
