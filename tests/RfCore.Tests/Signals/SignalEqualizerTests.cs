// The far end's equalizer, frequency offset and bit errors (brief PCH-4). Expected numbers are closed forms written
// here; the received records are an OFDM carrier's own record through a channel written here, with seeded noise.
//
//   Q3  With noise at 40 dB SNR per resource element, the pilot equalizer's EVM is within 0.5 dB of the genie's
//       through a static two-path channel inside the cyclic prefix.
//   Q4  A 5 kHz offset is reported to 1 Hz, and with it removed the genie EVM is the EVM with no offset to 1e-3 %.
//   Q5  Counted QPSK and 16QAM BER in AWGN at three Eb/N0 each: the closed form lies in the counted BER's exact 95 %
//       interval.
//   Q6  At Eb/N0 = 12 dB, QPSK's semi-analytic BER is the closed form to 1 %.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using RfCore.Statistics;
using Xunit;

namespace RfCore.Tests.Signals;

public class SignalEqualizerTests
{
    private static CarrierRecord Record(QamOrder m, double seconds, out double recordS)
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 5e6, ScsHz = 15e3, Modulation = m, Seed = 7 };
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [c], RecordSeconds = seconds });
        recordS = sig.RecordSeconds;
        return sig.Carriers[0];
    }

    private static Complex Gauss(Random r, double sigmaPerAxis)
    {
        double u1 = 1 - r.NextDouble(), u2 = r.NextDouble(), a = Math.Sqrt(-2 * Math.Log(u1));
        return new Complex(a * Math.Cos(2 * Math.PI * u2), a * Math.Sin(2 * Math.PI * u2)) * sigmaPerAxis;
    }

    /// <summary>The record through x[n] + a·x[n − delay] (periodic, as the record is).</summary>
    private static Complex[] TwoPath(Complex[] x, int delay, double a)
        => [.. Enumerable.Range(0, x.Length).Select(n => x[n] + a * x[((n - delay) % x.Length + x.Length) % x.Length])];

    private static double Db(double pct) => 20 * Math.Log10(pct / 100);

    [Fact]
    public void Q3_PilotIsWithinHalfADecibelOfGenie_At40dB()
    {
        var rec = Record(QamOrder.Qam16, 10e-3, out double rs);
        var nm = OfdmGenerator.Numerology(rec.Spec);
        var y = TwoPath(rec.Native, 2, 0.5);                       // 2 samples, well inside the cyclic prefix
        // 40 dB per resource element: the record's power spreads over the occupied subcarriers, the noise over the FFT
        double p = y.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary);
        double noise = p * nm.NSc / nm.Nfft * 1e-4;
        var rnd = new Random(11);
        for (int i = 0; i < y.Length; i++) y[i] += Gauss(rnd, Math.Sqrt(noise / 2));
        var d = SignalEvm.Demodulate(rec, rs, y, rec.NativeRateHz, 0, 0, out var why)!;
        Assert.Null(why);
        var genie = SignalEqualizer.Ofdm(d, new EqualizerSettings(EqualizerMode.Genie));
        var pilot = SignalEqualizer.Ofdm(d, new EqualizerSettings(EqualizerMode.Pilot));
        Assert.Null(pilot.Note);
        Assert.InRange(Db(genie.RmsPercent), -41.5, -38.5);
        Assert.True(Math.Abs(Db(pilot.RmsPercent) - Db(genie.RmsPercent)) < 0.5,
                    $"pilot {Db(pilot.RmsPercent):0.00} dB, genie {Db(genie.RmsPercent):0.00} dB");
        Assert.True(pilot.Points.Length < genie.Points.Length);       // the pilots themselves are not measured
    }

    [Fact]
    public void Q4_AnOffsetIsFound_AndRemovedBeforeTheEqualizer()
    {
        var rec = Record(QamOrder.Qam64, 1e-3, out double rs);
        double fs = rec.NativeRateHz;
        var y = rec.Native.Select((v, n) => v * Complex.FromPolarCoordinates(1.0, 2 * Math.PI * 5000 * n / fs)).ToArray();
        ReceivedSymbols? Read(Complex[] x, CarrierOffset o) => SignalEvm.Demodulate(rec, rs, x, fs, 0, o.FrequencyHz, out _, o.DriftHzps);

        // the first estimate reads through a third of a subcarrier's inter-carrier interference; each pass removes what
        // the last left, and the total is what every Equalizer reports (none then measures the window as it came)
        var first = Read(y, CarrierOffset.Zero)!;
        var fixedD = SignalEqualizer.RemoveOffset(first, o => Read(y, o), SignalEqualizer.EstimateOffset, out var total);
        Assert.Equal(5000, total.FrequencyHz, 1.0);

        var genie = new EqualizerSettings(EqualizerMode.Genie);
        double with = SignalEqualizer.Ofdm(fixedD, genie).RmsPercent;
        double without = SignalEqualizer.Ofdm(Read(rec.Native, CarrierOffset.Zero)!, genie).RmsPercent;
        Assert.True(Math.Abs(with - without) < 1e-3, $"EVM {with} % with the offset removed, {without} % with none");
    }

    /// <summary>A carrier's ideal points with AWGN of Es/N0 = <paramref name="esN0"/> (linear) on every element, the noise
    /// scaled to that power exactly when <paramref name="exact"/>.</summary>
    private static ReceivedSymbols Noisy(QamOrder m, double esN0, int seed, bool exact = false)
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 5e6, ScsHz = 15e3, Modulation = m, Seed = seed };
        var nm = OfdmGenerator.Numerology(c);
        var ideal = OfdmGenerator.IdealSymbols(c, 20).SelectMany(r => r).ToArray();
        var rnd = new Random(1000 + seed);                       // never the generator's own seed: its stream draws the points
        var n = ideal.Select(_ => Gauss(rnd, Math.Sqrt(0.5 / esN0))).ToArray();
        if (exact)
        {
            double scale = Math.Sqrt(1 / esN0 / n.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary));
            n = [.. n.Select(v => v * scale)];
        }
        var hz = Enumerable.Range(0, nm.NSc).Select(i => { int b = OfdmGenerator.BinOf(nm, i); return (b < nm.Nfft / 2 ? b : b - nm.Nfft) * nm.ScsHz; }).ToArray();
        var rx = ideal.Select((v, i) => v + n[i]).ToArray();
        return new ReceivedSymbols(c, rx, ideal, ideal, hz, ideal.Length / nm.NSc, true);
    }

    private static double Q(double x) => 0.5 * Normal.Erfc(x / Math.Sqrt(2));

    [Theory]
    [InlineData(QamOrder.Qpsk, 0.0)]
    [InlineData(QamOrder.Qpsk, 4.0)]
    [InlineData(QamOrder.Qpsk, 7.0)]
    [InlineData(QamOrder.Qam16, 4.0)]
    [InlineData(QamOrder.Qam16, 8.0)]
    [InlineData(QamOrder.Qam16, 10.0)]
    public void Q5_CountedBer_HoldsTheClosedFormInItsInterval(QamOrder m, double ebN0Db)
    {
        int k = (int)m;
        double gb = Math.Pow(10, ebN0Db / 10);
        var d = Noisy(m, k * gb, seed: 3);
        var eq = SignalEqualizer.Ofdm(d, EqualizerSettings.Off);
        var count = SignalDecisions.Qam(m, eq.Points, eq.Ideal);
        double closed = m == QamOrder.Qpsk
            ? Q(Math.Sqrt(2 * gb))
            : (3 * Q(Math.Sqrt(0.8 * gb)) + 2 * Q(3 * Math.Sqrt(0.8 * gb)) - Q(5 * Math.Sqrt(0.8 * gb))) / 4;
        var (lo, hi) = count.BerInterval;
        Assert.True(count.BitErrors >= 50, $"{count.BitErrors} errors");
        Assert.InRange(closed, lo, hi);
        Assert.Equal(count.Bits, d.Received.Length * (long)k);
    }

    [Fact]
    public void Q6_TheSemiAnalyticBer_IsTheClosedForm()
    {
        double gb = Math.Pow(10, 1.2);
        var eq = SignalEqualizer.Ofdm(Noisy(QamOrder.Qpsk, 2 * gb, seed: 5, exact: true), EqualizerSettings.Off);
        double est = SignalDecisions.SemiAnalytic(eq.Sinr, g => SignalDecisions.QamBer(QamOrder.Qpsk, g));
        double closed = Q(Math.Sqrt(2 * gb));
        Assert.True(Math.Abs(est / closed - 1) < 0.01, $"BERest {est:G4}, closed form {closed:G4}");
        // the exact interval of no errors in 100 trials reaches 1 − 0.025^(1/100)
        Assert.Equal(1 - Math.Pow(0.025, 0.01), BetaFunction.BinomialInterval(0, 100).High, 1e-9);
    }

    /// <summary>A single carrier's offset at a moderate SNR: each point is one noisy symbol, and unwrapped step to step a pair of
    /// noisy phases more than π apart slipped a cycle into every later point (16QAM at Es/N0 12 dB read hundreds of Hz rms). 300 Hz on
    /// 511 16QAM symbols at 1 Msps, Es/N0 12 dB, 20 seeds: each estimate within 50 Hz (the bound on its spread is ~9 Hz rms).</summary>
    [Fact]
    public void ScOffset_AtModerateSnr_DoesNotSlipCycles()
    {
        const int sps = ScQam.SamplesPerSymbol, n = 511;
        const double rs = 1e6, offset = 300, esn0 = 15.85;   // 12 dB
        double fs = rs * sps;
        double[] levels = [-3, -1, 1, 3];
        double sigma = Math.Sqrt(1 / esn0 / 2);              // per axis, unit mean symbol energy
        for (int seed = 1; seed <= 20; seed++)
        {
            var r = new Random(seed);
            var x = new Complex[n * sps];
            var sent = new Complex[n * sps];
            var at = new int[n];
            for (int k = 0; k < n; k++)
            {
                int i = k * sps;
                at[k] = i;
                sent[i] = new Complex(levels[r.Next(4)], levels[r.Next(4)]) / Math.Sqrt(10);
                x[i] = sent[i] * Complex.FromPolarCoordinates(1, 2 * Math.PI * offset * i / fs) + Gauss(r, sigma);
            }
            var w = new ReceivedWaveform(null!, QualityKind.Sc, x, sent, at, sps, 0, x.Length, fs, 0, true);
            double got = SignalEqualizer.EstimateOffset(w).FrequencyHz;
            Assert.True(Math.Abs(got - offset) < 50, $"seed {seed}: {got:F1} Hz against {offset} Hz");
        }
    }
}
