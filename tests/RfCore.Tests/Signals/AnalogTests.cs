// The analog carriers (AM, DSB-SC, SSB, FM, PM) and the navigation composites of ICAO Annex 10 Vol. I, 7th edition
// (2018; no DOI): VOR 3.3.5, ILS localizer 3.1.3.5 and glide path 3.1.5.5. Bessel lines: the generating function at t = e^{jθ},
// e^{jβ sin θ} = Σ J_k(β) e^{jkθ} (NIST DLMF 10.12.1), and Bessel's integral for J_k (DLMF 10.9.2)
// (https://dlmf.nist.gov/10.12, https://dlmf.nist.gov/10.9; no DOI).
//
//   A1  AM: the carrier and the two sidebands at m/2 of it; the depth read back.
//   A2  FM at index 2.405: the carrier nulled (J0's first zero), the lines at J_k(2.405); the deviation read back.
//   A3  SSB: the other sideband more than 40 dB down (USB and LSB).
//   A4  VOR: the bearing read back within 0.5 deg, with and without the ident; 30 % AM, ±480 Hz subcarrier.
//   A5  ILS LOC and GS: DDM read back within 0.001, SDM 0.40 and 0.80.
//   A6  The record is periodic: two records' worth is one record twice; noise stays in its band.
//   A7  Every preset reads back what it sets; refusals name the field.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class AnalogTests
{
    private static double Db(double r) => 20 * Math.Log10(r);

    /// <summary>|X[k]| / N: the amplitude of the line at bin k (negative k counts from the top).</summary>
    private static Func<int, double> Lines(Complex[] x)
    {
        var X = Fft.Forward(x);
        return k => X[(k + X.Length) % X.Length].Magnitude / X.Length;
    }

    /// <summary>J_k(β) by Bessel's integral, (1/π)∫₀^π cos(kτ − β sin τ) dτ (DLMF 10.9.2), trapezoid on a periodic integrand.</summary>
    private static double BesselJ(int k, double beta)
    {
        const int m = 4096;
        double sum = 0;
        for (int i = 0; i < m; i++) { double tau = 2 * Math.PI * i / m; sum += Math.Cos(k * tau - beta * Math.Sin(tau)); }
        return sum / m;
    }

    [Fact]
    public void A1_AmCarrierAndSidebands()
    {
        var s = new AnalogShape(AnalogKind.Am, Depth: 0.3);
        var x = Analog.Generate(s);
        var a = Lines(x);
        int k = (int)Math.Round(1e3 * Analog.Validate(s).RecordSeconds);
        Assert.Equal(Db(0.15), Db(a(k) / a(0)), 2);
        Assert.Equal(Db(0.15), Db(a(-k) / a(0)), 2);
        var r = Analog.Measure(s, x);
        Assert.Equal((1e3, 0.3), (r.MessageHz, Math.Round(r.Depth, 9)));
    }

    [Fact]
    public void A2_FmBesselLinesAtCarrierNull()
    {
        var s = new AnalogShape(AnalogKind.Fm, Index: 2.405);
        var x = Analog.Generate(s);
        var a = Lines(x);
        int k = (int)Math.Round(1e3 * Analog.Validate(s).RecordSeconds);
        Assert.True(Db(a(0)) < -60, $"carrier {Db(a(0)):0.0} dB");
        for (int j = 1; j <= 4; j++)
        {
            Assert.Equal(Db(BesselJ(j, 2.405)), Db(a(j * k)), 2);
            Assert.Equal(Db(BesselJ(j, 2.405)), Db(a(-j * k)), 2);
        }
        Assert.Equal(2405, Analog.Measure(s, x).DeviationHz, 6);
    }

    [Fact]
    public void A3_SsbSuppressesTheOtherSideband()
    {
        foreach (var (kind, sign) in new[] { (AnalogKind.Usb, 1), (AnalogKind.Lsb, -1) })
        {
            var s = new AnalogShape(kind);
            var x = Analog.Generate(s);
            var a = Lines(x);
            int k = (int)Math.Round(1e3 * Analog.Validate(s).RecordSeconds);
            Assert.True(Db(a(-sign * k) / a(sign * k)) < -40);
            Assert.Equal(1e3, Analog.Measure(s, x).MessageHz);
        }
    }

    [Fact]
    public void A4_VorBearingReadBack()
    {
        foreach (var (bearing, ident) in new[] { (0.0, ""), (45.0, ""), (137.3, ""), (270.0, ""), (359.6, "VOR") })
        {
            var s = new AnalogShape(AnalogKind.Vor, BearingDeg: bearing, Ident: ident);
            var r = Analog.Measure(s, Analog.Generate(s));
            double err = ((r.BearingDeg - bearing) % 360 + 540) % 360 - 180;
            Assert.True(Math.Abs(err) < 0.5, $"bearing {bearing}: read {r.BearingDeg}");
            Assert.Equal(0.3, r.Depth, 4);                                       // the keyed ident leaks ~1e-5
            Assert.Equal(480, r.DeviationHz, 3);
        }
    }

    [Fact]
    public void A5_IlsDdmReadBack()
    {
        foreach (var (kind, ddm, sdm) in new[] { (AnalogKind.Loc, 0.0, 0.4), (AnalogKind.Loc, 0.155, 0.4), (AnalogKind.Loc, -0.0931, 0.4),
                                                  (AnalogKind.Gs, 0.0875, 0.8), (AnalogKind.Gs, -0.175, 0.8) })
        {
            var s = new AnalogShape(kind, Ddm: ddm, Ident: kind == AnalogKind.Loc ? "IAB" : "");
            var r = Analog.Measure(s, Analog.Generate(s));
            Assert.True(Math.Abs(r.Ddm - ddm) < 0.001, $"{kind} DDM {ddm}: read {r.Ddm}");
            Assert.Equal(sdm, r.Sdm, 4);
        }
    }

    [Fact]
    public void A6_PeriodicAndBandLimited()
    {
        foreach (var s in new[] { new AnalogShape(AnalogKind.Fm, [new(1e3), new(2.7e3, -6, 30)], DeviationHz: 3e3),
                                  new AnalogShape(AnalogKind.Vor, BearingDeg: 30) })
        {
            var one = Analog.Generate(s);
            var two = Analog.Generate(s with { RecordSeconds = 2 * Analog.Step });
            Assert.Equal(2 * one.Length, two.Length);
            for (int i = 0; i < two.Length; i++) Assert.True((two[i] - one[i % one.Length]).Magnitude < 1e-9);
        }

        var dsb = new AnalogShape(AnalogKind.DsbSc, NoiseBandwidthHz: 3e3, Seed: 7);
        var X = Fft.Forward(Analog.Generate(dsb));
        int n = X.Length, kb = (int)Math.Round(3e3 * Analog.Validate(dsb).RecordSeconds);
        double inBand = 0, total = X.Sum(v => v.Magnitude * v.Magnitude);
        for (int k = -kb; k <= kb; k++) inBand += Math.Pow(X[(k + n) % n].Magnitude, 2);
        Assert.Equal(1, inBand / total, 12);
    }

    [Fact]
    public void A7_PresetsReadBackAndRefusals()
    {
        foreach (var p in Analog.Presets)
        {
            var s = Analog.Validate(p.Shape);
            var r = Analog.Measure(s, Analog.Generate(s));
            switch (s.Kind)
            {
                case AnalogKind.Am: Assert.Equal(s.Depth, r.Depth, 9); break;
                case AnalogKind.Fm: Assert.Equal(s.DeviationHz, r.DeviationHz, 3); break;
                case AnalogKind.Vor: Assert.Equal(s.BearingDeg, r.BearingDeg, 3); break;
                case AnalogKind.Usb: Assert.Equal(0.5, r.MessageLevel, 6); break;
                default: Assert.True(Math.Abs(r.Ddm - s.Ddm) < 1e-6, p.Name); break;
            }
        }

        foreach (var (s, fragment) in new[]
        {
            (new AnalogShape(AnalogKind.Am, Depth: 1.5), "Depth must be"),
            (new AnalogShape(AnalogKind.Gs, Ident: "ABC"), "Ident belongs to a VOR or ILS LOC"),
            (new AnalogShape(AnalogKind.Loc, Ddm: 0.5), "|DDM| at most SDM"),
            (new AnalogShape(AnalogKind.Fm, []), "needs a tone or a noise bandwidth"),
            (new AnalogShape(AnalogKind.Am, [new(1)]), "a tone must be at least 1/Record"),
        })
            Assert.Contains(fragment, Assert.Throws<FormatException>(() => Analog.Validate(s)).Message, StringComparison.Ordinal);
    }
}
