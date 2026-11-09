// The received quality of a DBPSK, FSK or CSS carrier (brief NZ-8, SignalQuality).
//
//   Q1  The carrier's own record, resampled exactly to 4x its rate, reads zero error: DBPSK EVM, FSK error (with the
//       deviation exactly the carrier's and no offset) and CSS's de-chirped error.
//   Q2  Delayed by a fraction of a sample, the delay is found and taken out.
//   Q3  With white noise added, the error is the noise the measurement filter passes over the signal: DBPSK's EVM² and
//       CSS's error² are 1/SNR, the SNR read from the same filtered samples (SignalToNoise against the clean record),
//       to the record's statistics. FSK's error is set by the noise and nothing else: it rises with it.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class SignalQualityTests
{
    public static TheoryData<string> Carriers => new()
    {
        "DBPSK;Rb=1kbps;Data=0110100111;Off=0;P=0",
        "FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0",
        "CSS;SF=7;BW=125kHz;Off=0;P=0",
    };

    private static (CarrierRecord rec, double record) Make(string carrier)
    {
        var sig = SignalComposer.Generate(new SignalSpec { Carriers = [SignalSpec.ParseCarrier(carrier)], RecordSeconds = 0.5e-3 });
        return (sig.Carriers[0], sig.RecordSeconds);
    }

    private static Complex[] At4x(CarrierRecord rec) => Fft.ResamplePeriodic(rec.Native, 4 * rec.Native.Length);

    [Theory]
    [MemberData(nameof(Carriers))]
    public void Q1_TheCarriersOwnRecord_ReadsZero(string carrier)
    {
        var (rec, record) = Make(carrier);
        var r = SignalQuality.Receive(rec, record, At4x(rec), 4 * rec.NativeRateHz, 0, 0, out var why);
        Assert.Null(why);
        var q = SignalQuality.Evaluate(r!);
        Assert.True(q.ErrorPercent < 1e-6, $"{q.Kind}: {q.ErrorPercent} %");
        Assert.Equal(0, q.DelayS);
        if (q.Kind == QualityKind.Fsk)
        {
            Assert.Equal(45e3, q.DeviationHz, 1e-6);
            Assert.Equal(0, q.OffsetHz, 1e-6);
        }
    }

    [Theory]
    [MemberData(nameof(Carriers))]
    public void Q2_ADelay_IsFoundAndTakenOut(string carrier)
    {
        var (rec, record) = Make(carrier);
        double fs = 4 * rec.NativeRateHz, tau = 0.37 / rec.NativeRateHz;
        var spec = Fft.Forward(At4x(rec));
        for (int k = 0; k < spec.Length; k++)
            spec[k] *= Complex.FromPolarCoordinates(1.0, -2 * Math.PI * (k < spec.Length / 2 ? k : k - spec.Length) * fs / spec.Length * tau);
        var q = SignalQuality.Evaluate(SignalQuality.Receive(rec, record, Fft.Inverse(spec), fs, 0, 0, out _)!);
        Assert.Equal(tau, q.DelayS, 1e-6 * tau + 1e-15);
        Assert.True(q.ErrorPercent < 1e-3, $"{q.Kind}: {q.ErrorPercent} %");
    }

    [Theory]
    [MemberData(nameof(Carriers))]
    public void Q3_Noise_ReadsAsTheSnrSays(string carrier)
    {
        var (rec, record) = Make(carrier);
        double fs = 4 * rec.NativeRateHz;
        var clean = At4x(rec);
        var rng = new Random(7);
        double sigma = 0.02;
        var noisy = clean.Select(v => v + sigma * new Complex(Gauss(rng), Gauss(rng))).ToArray();
        var rq = SignalQuality.Receive(rec, record, clean, fs, 0, 0, out _)!;
        var rn = SignalQuality.Receive(rec, record, noisy, fs, 0, 0, out _)!;
        var q = SignalQuality.Evaluate(rn);
        double snr = SignalQuality.SignalToNoise(rn, rq);
        if (q.Kind == QualityKind.Fsk)
        {
            Assert.InRange(q.ErrorPercent, 0.1, 50);
            return;
        }
        double ratio = q.ErrorPercent * q.ErrorPercent / 1e4 * snr;
        // EVM² over N instants (DBPSK) or chirps' samples (CSS) has a relative standard error of about 1/sqrt(N)
        int n = q.Kind == QualityKind.Dbpsk ? rn.At.Length : rn.At.Length * rn.Span / Css.SamplesPerChip;
        Assert.True(Math.Abs(ratio - 1) < 4 / Math.Sqrt(n), $"{q.Kind}: EVM² x SNR = {ratio:F4} over {n}");
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()) / Math.Sqrt(2);
}
