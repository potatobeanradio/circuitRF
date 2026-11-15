// The single-carrier PSK / QAM / APSK carrier (brief PCH-5). Expected numbers are closed forms written here, or the
// independent simulation testdata/propagation/apsk_ber_reference.py (its committed JSON).
//
//   S1  Gate 1: the native record, demodulated as it stands, reads EVM 0 to 1e-9 % for every modulation.
//   S2  Gate 2: the spectrum ends at Rs(1 + α)/2 to one bin; the matched-filtered record at the symbol instants is the
//       ideal points to 1e-6 (no intersymbol interference), with a preamble and pilot blocks in the record.
//   S3  Gate 3 (the delay): the record delayed by 3.3 symbols reads its delay to 1e-12 s and the native EVM to 1e-5 %
//       (the delay fit's own resolution). The pad half of the gate is EnvelopeScTests.
//   S4  Gate 4: 8PSK and 16APSK through AWGN at Es/N0 set on the record: the counted BER's exact 95 % interval holds
//       the closed form (8PSK) and the independent simulation's BER (16APSK).
//   S5  The carrier's text round-trips, and a field another carrier owns is refused.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class ScQamTests
{
    private static (CarrierRecord rec, double record) Make(string carrier)
    {
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier(carrier)] });
        return (sig.Carriers[0], sig.RecordSeconds);
    }

    private static ReceivedWaveform Receive(CarrierRecord rec, double record, Complex[] y)
    {
        var w = SignalQuality.Receive(rec, record, y, rec.NativeRateHz, 0, 0, out var why);
        Assert.Null(why);
        return w!;
    }

    [Theory]
    [InlineData("BPSK")]
    [InlineData("QPSK")]
    [InlineData("8PSK")]
    [InlineData("16QAM")]
    [InlineData("64QAM")]
    [InlineData("256QAM")]
    [InlineData("16APSK")]
    [InlineData("32APSK")]
    public void S1_TheNativeRecord_ReadsZeroEvm(string mod)
    {
        var (rec, record) = Make($"SC;Rs=1Msps;Mod={mod};RollOff=0.25;Data=PN9;Off=0;P=0");
        var q = SignalQuality.Evaluate(Receive(rec, record, rec.Native));
        Assert.True(q.ErrorPercent < 1e-9, $"{mod}: {q.ErrorPercent} %");
        Assert.Equal(0, q.DelayS);
    }

    [Fact]
    public void S2_TheSpectrumEndsAtRsTimesOnePlusAlpha_AndTheEyeIsOpen()
    {
        const double rs = 2e6, alpha = 0.35;
        var (rec, record) = Make($"SC;Rs=2Msps;Mod=16APSK;RollOff={alpha};Data=PN9;Preamble=32;PilotBlock=8/100;Off=0;P=0");
        var x = rec.Native;
        int n = x.Length;
        double bin = rec.NativeRateHz / n;
        var spec = Fft.Forward(x);
        double top = spec.Max(v => v.Magnitude);
        // the highest frequency either side holding any power, against Rs(1 + α)/2
        double hi = 0, lo = 0;
        for (int k = 0; k < n; k++)
        {
            if (spec[k].Magnitude < 1e-9 * top) continue;
            double f = (k <= n / 2 ? k : k - n) * bin;
            hi = Math.Max(hi, f); lo = Math.Min(lo, f);
        }
        Assert.InRange(hi - lo, rs * (1 + alpha) - 2 * bin, rs * (1 + alpha) + bin);
        Assert.Equal(rs * (1 + alpha), ScQam.BandwidthHz(ScQam.Resolve(rec.Spec)), 1e-6);

        // the matched-filtered record at each symbol instant, over one gain, is the ideal point
        var shape = ScQam.Resolve(rec.Spec);
        var mf = ScQam.Filter(shape, x, rec.NativeRateHz, matched: true);
        var ideal = ScQam.Symbols(shape).Points;
        var at = ideal.Select((_, k) => mf[k * ScQam.SamplesPerSymbol]).ToArray();
        Complex num = 0; double den = 0;
        for (int k = 0; k < at.Length; k++) { num += at[k] * Complex.Conjugate(ideal[k]); den += ideal[k].Magnitude * ideal[k].Magnitude; }
        var g = num / den;
        double isi = at.Select((v, k) => (v / g - ideal[k]).Magnitude).Max();
        Assert.True(isi < 1e-6, $"worst intersymbol interference {isi:G3}");
        Assert.Equal(32 + 8 * ((shape.DataSymbols - 1) / 100), shape.KnownSymbols);
        Assert.Equal(shape.RecordSymbols / rs, record, 1e-12);
    }

    [Fact]
    public void S3_ADelayOf3Point3Symbols_IsRecovered()
    {
        var (rec, record) = Make("SC;Rs=1Msps;Mod=8PSK;RollOff=0.2;Data=PN9;Off=0;P=0");
        double tau = 3.3 / 1e6, fs = rec.NativeRateHz;
        var spec = Fft.Forward(rec.Native);
        int n = spec.Length;
        for (int k = 0; k < n; k++) spec[k] *= Complex.FromPolarCoordinates(1.0, -2 * Math.PI * (k <= n / 2 ? k : k - n) * fs / n * tau);
        var q = SignalQuality.Evaluate(Receive(rec, record, Fft.Inverse(spec)));
        Assert.Equal(tau, q.DelayS, 1e-12);
        // the delay fit lands within ~3e-15 s, which leaves 1.4e-6 % of EVM over the native record's 1e-12 %
        Assert.True(q.ErrorPercent < 1e-5, $"{q.ErrorPercent} %");
    }

    /// <summary>The native record with white noise at Es/N0 = <paramref name="esN0"/> (linear): per sample, N0·fs = sps/γ
    /// on a unit-power record, read through the matched filter with one gain.</summary>
    private static ErrorCount Counted(string mod, double esN0, int seed, out Equalized eq)
    {
        var (rec, record) = Make($"SC;Rs=1Msps;Mod={mod};RollOff=0.35;Data=PN15;Off=0;P=0");
        var rnd = new Random(seed);
        double sigma = Math.Sqrt(ScQam.SamplesPerSymbol / esN0 / 2);
        var y = rec.Native.Select(v =>
        {
            double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble(), a = Math.Sqrt(-2 * Math.Log(u1)) * sigma;
            return v + new Complex(a * Math.Cos(2 * Math.PI * u2), a * Math.Sin(2 * Math.PI * u2));
        }).ToArray();
        eq = SignalEqualizer.Sc(Receive(rec, record, y), EqualizerSettings.Off);
        return SignalDecisions.Sc(ScQam.Constellation(ScQam.Resolve(rec.Spec)), eq.Points, eq.Ideal);
    }

    [Theory]
    [InlineData(6.0)]
    [InlineData(9.0)]
    public void S4a_8Psk_CountedBer_HoldsTheClosedForm(double ebN0Db)
    {
        double gb = Math.Pow(10, ebN0Db / 10);
        var count = Counted("8PSK", 3 * gb, seed: 5, out var eq);
        double closed = ScQam.Psk8Ber(3 * gb);
        var (lo, hi) = count.BerInterval;
        Assert.True(count.BitErrors >= 50, $"{count.BitErrors} errors");
        Assert.InRange(closed, lo, hi);
        // the noise reaches the instants at Es/N0: EVM² = 1/γ to the record's statistics
        Assert.Equal(1, eq.RmsPercent * eq.RmsPercent / 1e4 * 3 * gb, 0.03);
    }

    [Theory]
    [InlineData(8.0)]
    [InlineData(10.0)]
    public void S4b_16Apsk_CountedBer_HoldsTheIndependentSimulation(double ebN0Db)
    {
        string path = Find("apsk_ber_reference.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        double reference = doc.RootElement.GetProperty("points").EnumerateArray()
                              .Single(p => p.GetProperty("EbN0_dB").GetDouble() == ebN0Db).GetProperty("ber").GetDouble();
        var count = Counted("16APSK", 4 * Math.Pow(10, ebN0Db / 10), seed: 9, out _);
        var (lo, hi) = count.BerInterval;
        Assert.True(count.BitErrors >= 50, $"{count.BitErrors} errors");
        Assert.InRange(reference, lo, hi);
    }

    private static string Find(string name)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var p = Path.Combine(dir, "testdata", "propagation", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("testdata/propagation/" + name + " not found");
    }

    [Fact]
    public void S5_TheCarrierText_RoundTrips()
    {
        const string text = "SC;Rs=10Msps;Mod=32APSK;RollOff=0.2;Span=16;Rings=2.84,5.27;Phases=45,15,0;Data=PN15;Symbols=4096;Preamble=64;PilotBlock=36/1440;Off=0;P=-3";
        var c = SignalSpec.ParseCarrier(text);
        Assert.Equal(text, SignalSpec.FormatCarrier(c));
        Assert.Equal(10e6 * 1.2, c.BandwidthHz, 1e-6);
        Assert.IsType<ScCarrierRefusal>(Assert.ThrowsAny<FormatException>(() => SignalSpec.ParseCarrier("SC;Rs=1Msps;Mod=QPSK;Rings=2.85")));
        Assert.Throws<FormatException>(() => SignalSpec.ParseCarrier("DBPSK;RollOff=0.3"));
        var shape = ScQam.Resolve(c);
        Assert.Equal(4096 + 64 + 36 * 2, shape.RecordSymbols);   // blocks after data symbols 1440 and 2880
    }
}
