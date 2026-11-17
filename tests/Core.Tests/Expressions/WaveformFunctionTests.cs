using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-run-display-hb-preset-and-units gate 3 — <c>waveform(x[, f0])</c>, a spectrum as its waveform over one period, is
/// <c>fft</c>'s inverse: a one-tone and a commensurate two-tone spectrum come back from <c>fft(waveform(x))</c> unchanged, its
/// rms is the spectrum's, and tones with no common period short enough to draw are refused by name.
/// </summary>
public sealed class WaveformFunctionTests
{
    private static Value Eval(string expr, string cube, DataCube c)
    {
        var ds = new DataSet();
        ds.Add(cube, c);
        return new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["HB1"] = ds })).Eval(expr, new Scope("t"));
    }

    /// <summary>A one-tone spectrum on a <c>harmonic</c> axis, as harmonic balance writes one: DC 0.3, then 1.2∠30°,
    /// 0.4∠−70°, 0.05∠150°.</summary>
    private static readonly Complex[] OneTone =
    [
        new(0.3, 0), Complex.FromPolarCoordinates(1.2, 30 * Math.PI / 180),
        Complex.FromPolarCoordinates(0.4, -70 * Math.PI / 180), Complex.FromPolarCoordinates(0.05, 150 * Math.PI / 180),
    ];

    private static DataCube Harmonic(Complex[] x)
        => new([new Axis("harmonic", [.. Enumerable.Range(0, x.Length).Select(k => (double)k)], "")], x) { Unit = "V" };

    [Fact]
    public void OneTone_FftOfTheWaveformIsTheSpectrum_AndRmsIsTheSpectrums()
    {
        const double f0 = 50;
        var spec = Eval("fft(waveform(HB1.V, 50), 50)", "V", Harmonic(OneTone)).AsCube();
        Assert.Equal("harmonic", spec.Axes[^1].Name);
        for (int k = 0; k < OneTone.Length; k++)
            Assert.True((spec.ComplexValues[k] - OneTone[k]).Magnitude <= 1e-9 * OneTone[1].Magnitude,
                $"harmonic {k}: {spec.ComplexValues[k]} against {OneTone[k]}");

        var wave = Eval("waveform(HB1.V, 50)", "V", Harmonic(OneTone)).AsCube();
        Assert.Equal("time", wave.Axes[0].Name);
        Assert.Equal(1 / f0, wave.Axes[0].Values[^1], 12);
        Assert.Equal("V", wave.Unit);

        double rms = Math.Sqrt(OneTone[0].Real * OneTone[0].Real + OneTone.Skip(1).Sum(z => z.Magnitude * z.Magnitude) / 2);
        Assert.Equal(rms, Eval("rms(waveform(HB1.V, 50))", "V", Harmonic(OneTone)).AsReal(), 9);
        // the measurements page's example: the peak, through max_over along the time axis
        Assert.Equal(wave.RealValues.Max(), Eval("max_over(waveform(HB1.V, 50), \"time\")", "V", Harmonic(OneTone)).AsReal(), 12);
    }

    [Fact]
    public void TwoTone_CommonPeriodFound_AndRoundTrips()
    {
        // 50 Hz and 1 kHz, their products on a mixIndex axis in hertz (a negative-frequency product stored conjugated)
        double[] hz = [0, 50, 1000, 950, -900, 100];
        Complex[] x = [new(0.1, 0), new(1, 0.2), new(0.3, -0.1), new(0.02, 0.01), new(0.01, -0.03), new(0.05, 0.05)];
        var c = new DataCube([new Axis("mixIndex", hz, "Hz")], x);
        var wave = Eval("waveform(HB1.V)", "V", c).AsCube();
        Assert.Equal(1 / 50.0, wave.Axes[0].Values[^1], 12);   // the tones' common period

        // fft at 50 Hz reads harmonics up to 16 (800 Hz): 1, 2 and |−900|/50 = 18 is out of its reach; 950 and 1000 too
        var spec = Eval("fft(waveform(HB1.V), 50)", "V", c).AsCube().ComplexValues;
        Assert.True((spec[0] - x[0]).Magnitude <= 1e-9, $"DC {spec[0]}");
        Assert.True((spec[1] - x[1]).Magnitude <= 1e-9, $"50 Hz {spec[1]}");
        Assert.True((spec[2] - x[5]).Magnitude <= 1e-9, $"100 Hz {spec[2]}");
        // the waveform is the sum, sample by sample
        double t = wave.Axes[0].Values[7];
        double expected = hz.Zip(x).Sum(p => (p.Second * Complex.Exp(new Complex(0, 2 * Math.PI * p.First * t))).Real);
        Assert.Equal(expected, wave.RealValues[7], 12);
    }

    [Fact]
    public void NoCommonPeriod_IsRefusedByName()
    {
        var c = new DataCube([new Axis("mixIndex", [0, 1e9, Math.Sqrt(2) * 1e9], "Hz")], [Complex.One, Complex.One, Complex.One]);
        var ex = Assert.Throws<ExpressionException>(() => Eval("waveform(HB1.V)", "V", c));
        Assert.Contains("no common period", ex.Message);
    }

    [Fact]
    public void HarmonicAxis_WithoutTheFundamental_IsRefused()
    {
        var ex = Assert.Throws<ExpressionException>(() => Eval("waveform(HB1.V)", "V", Harmonic(OneTone)));
        Assert.Contains("give the fundamental", ex.Message);
    }
}
