// The IEEE 802.11 OFDM packet. Expected numbers are the standard's, written here: IEEE Std 802.11-2020 (17.3.5.10 the
// pilot polarity, 17.3.8.x / 19.3.7 / 21.3.7 the tone plans, the masks of 17.3.9.3, 19.3.18.1, 21.3.17.1) and IEEE Std
// 802.11ax-2021 (27.3.2 the HE tone plan, 27.3.19.1 the mask, Table 27-49 the EVM limits).
//
//   W1  Each variant's spacing, FFT, data and pilot tone counts, pilot positions and occupied bandwidth; a data symbol
//       of the record carries energy on those tones and nowhere else.
//   W2  A clean packet of every variant reads EVM below -50 dB (the receiver's own floor).
//   W3  802.11ax 1024QAM at 80 MHz generates, measures below -50 dB, and states the -35 dB limit.
//   W4  A gain and a 2 kHz offset (taken out from the L-LTF, the residue by the pilots) leave EVM below -50 dB; 30 dB SNR reads about -30 dB.
//   W5  The spectrum at 4x fits inside the variant's mask.
//   W6  The data field's PAPR at 0.01 % is 9 to 12 dB at 4x oversampling; the idle gap adds -10 log10(duty) ungated.
//   W7  The pilot polarity sequence starts 1 1 1 1 -1 -1 -1 1; refusals name the field.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class WlanTests
{
    [Theory]
    [InlineData(WlanFormat.Legacy, 20, 312.5e3, 64, 48, new[] { -21, -7, 7, 21 }, 53)]
    [InlineData(WlanFormat.Ht, 20, 312.5e3, 64, 52, new[] { -21, -7, 7, 21 }, 57)]
    [InlineData(WlanFormat.Ht, 40, 312.5e3, 128, 108, new[] { -53, -25, -11, 11, 25, 53 }, 117)]
    [InlineData(WlanFormat.Vht, 80, 312.5e3, 256, 234, new[] { -103, -75, -39, -11, 11, 39, 75, 103 }, 245)]
    [InlineData(WlanFormat.Vht, 160, 312.5e3, 512, 468,
                new[] { -231, -203, -167, -139, -117, -89, -53, -25, 25, 53, 89, 117, 139, 167, 203, 231 }, 501)]
    [InlineData(WlanFormat.He, 20, 78.125e3, 256, 234, new[] { -116, -90, -48, -22, 22, 48, 90, 116 }, 245)]
    [InlineData(WlanFormat.He, 40, 78.125e3, 512, 468, new[] { -238, -212, -170, -144, -104, -78, -36, -10, 10, 36, 78, 104, 144, 170, 212, 238 }, 489)]
    [InlineData(WlanFormat.He, 80, 78.125e3, 1024, 980,
                new[] { -468, -400, -334, -266, -226, -158, -92, -24, 24, 92, 158, 226, 266, 334, 400, 468 }, 1001)]
    [InlineData(WlanFormat.He, 160, 78.125e3, 2048, 1960, null, 2025)]
    [InlineData(WlanFormat.Eht, 320, 78.125e3, 4096, 3920, null, 4073)]
    public void W1_tone_plan(WlanFormat f, int bwMHz, double df, int fft, int data, int[]? pilots, int spanTones)
    {
        var s = new WlanSpec(f, bwMHz * 1e6, Mcs: 0, DataSymbols: 1, IdleS: 0, WindowS: 0);
        var nm = Wlan.Numerology(s);
        Assert.Equal(df, nm.SubcarrierSpacingHz, 6);
        Assert.Equal(fft, nm.Fft);
        Assert.Equal(data, nm.DataTones.Length);
        if (pilots is not null) Assert.Equal(pilots, nm.PilotTones);
        else Assert.Equal(16 * bwMHz / 80, nm.PilotTones.Length);
        Assert.Equal(spanTones * df, nm.OccupiedBwHz, 3);

        // the last data symbol's useful part: energy on exactly the plan's tones
        var x = Wlan.Generate(s);
        var b = Fft.Forward(x.AsSpan(x.Length - fft));
        var on = nm.DataTones.Concat(nm.PilotTones).Select(k => (k % fft + fft) % fft).ToHashSet();
        double inPlan = 0, outPlan = 0;
        for (int i = 0; i < fft; i++) (on.Contains(i) ? ref inPlan : ref outPlan) += b[i].Magnitude * b[i].Magnitude;
        Assert.True(outPlan < 1e-20 * inPlan, $"{outPlan / inPlan:E2} of the energy off the plan");
        Assert.All(on, i => Assert.True(b[i].Magnitude > 1e-6));
    }

    [Theory]
    [InlineData(WlanFormat.Legacy, 20, 7, 0.8e-6)]
    [InlineData(WlanFormat.Ht, 40, 7, 0.4e-6)]
    [InlineData(WlanFormat.Vht, 80, 9, 0.8e-6)]
    [InlineData(WlanFormat.Vht, 160, 9, 0.4e-6)]
    [InlineData(WlanFormat.He, 20, 11, 3.2e-6)]
    [InlineData(WlanFormat.He, 160, 11, 1.6e-6)]
    [InlineData(WlanFormat.Eht, 320, 13, 0.8e-6)]
    public void W2_clean_packet_evm(WlanFormat f, int bwMHz, int mcs, double gi)
    {
        var s = new WlanSpec(f, bwMHz * 1e6, mcs, gi, DataSymbols: 6, Oversample: 2);
        var e = Wlan.Measure(s, Wlan.Generate(s));
        Assert.True(e.RmsDb < -50, $"{e.RmsDb:0.0} dB");
        Assert.Equal(6 * Wlan.Numerology(s).DataTones.Length, e.Received.Length);
    }

    [Fact]
    public void W3_he_1024qam_80mhz()
    {
        var s = new WlanSpec(WlanFormat.He, 80e6, Mcs: 11, DataSymbols: 10, Oversample: 2);
        var x = Wlan.Generate(s);
        Assert.Equal(Wlan.RecordSamples(s), x.Length);
        Assert.Equal(160e6, Wlan.SampleRateHz(s));
        var e = Wlan.Measure(s, x);
        Assert.True(e.RmsDb < -50, $"{e.RmsDb:0.0} dB");
        Assert.Equal(-35, Wlan.EvmLimitDb(s));
        Assert.Equal(100 * Math.Pow(10, -35 / 20.0), e.LimitPercent, 9);
        Assert.Equal(32, e.Ideal.Select(p => Math.Round(p.Real, 6)).Distinct().Count());   // 1024QAM: 32 levels on I
    }

    [Fact]
    public void W4_pilots_track_and_noise_reads()
    {
        var s = new WlanSpec(WlanFormat.Vht, 40e6, Mcs: 9, DataSymbols: 40);
        var x = Wlan.Generate(s);
        double fs = Wlan.SampleRateHz(s);
        var y = x.Select((v, n) => v * new Complex(0.3, -0.7) * Complex.FromPolarCoordinates(1, 2 * Math.PI * 2e3 * n / fs)).ToArray();
        Assert.True(Wlan.Measure(s, y).RmsDb < -50, $"{Wlan.Measure(s, y).RmsDb:0.0} dB with a 2 kHz offset");

        var rng = new Random(7);
        double sd = Math.Sqrt(1e-3 / 2);
        double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var z = x.Select(v => v + new Complex(sd * G(), sd * G())).ToArray();
        double db = Wlan.Measure(s, z).RmsDb;      // noise on the data, plus the one-LTF channel estimate's: ~-27 dB
        Assert.InRange(db, -31, -26);
    }

    [Theory]
    [InlineData(WlanFormat.Legacy, 20)]
    [InlineData(WlanFormat.Ht, 40)]
    [InlineData(WlanFormat.Vht, 80)]
    [InlineData(WlanFormat.He, 20)]
    [InlineData(WlanFormat.He, 80)]
    public void W5_spectrum_inside_mask(WlanFormat f, int bwMHz)
    {
        var s = new WlanSpec(f, bwMHz * 1e6, Mcs: 7, DataSymbols: 400 / (f >= WlanFormat.He ? 4 : 1), Oversample: 4);
        double fs = Wlan.SampleRateHz(s);
        var x = Wlan.Generate(s);
        int nseg = 1; while (fs / nseg > 100e3) nseg <<= 1;      // a resolution of 100 kHz or finer, as the mask is read
        var (fr, psd) = SignalMeasure.Welch(x, fs, nseg);
        double max = psd.Max();
        var mask = Wlan.Mask(s);
        double worst = double.MinValue, at = 0;
        for (int i = 0; i < fr.Length; i++)
        {
            double margin = 10 * Math.Log10(psd[i] / max) - mask.At(fr[i]);
            if (margin > worst) { worst = margin; at = fr[i]; }
        }
        Assert.True(worst <= 0, $"{worst:0.0} dB over the mask at {at / 1e6:0.00} MHz ({mask.Clause})");
    }

    [Theory]
    [InlineData(WlanFormat.Legacy, 20, 7)]
    [InlineData(WlanFormat.Vht, 80, 9)]
    [InlineData(WlanFormat.He, 80, 11)]
    public void W6_papr(WlanFormat f, int bwMHz, int mcs)
    {
        var s = new WlanSpec(f, bwMHz * 1e6, mcs, DataSymbols: 200 / (f >= WlanFormat.He ? 4 : 1), Oversample: 4);
        var x = Wlan.Generate(s);
        int packet = Wlan.PacketSamples(s);
        double gated = SignalMeasure.PaprDb(x.AsSpan(0, packet), 1e-4);
        Assert.InRange(gated, 9, 12);
        double ungated = SignalMeasure.PaprDb(x, 1e-4);
        Assert.InRange(ungated - gated, -10 * Math.Log10(Wlan.DutyCycle(s)) - 0.3, -10 * Math.Log10(Wlan.DutyCycle(s)) + 0.3);
    }

    [Fact]
    public void W7_polarity_and_refusals()
    {
        Assert.Equal(new[] { 1, 1, 1, 1, -1, -1, -1, 1 }, Wlan.Polarity.Take(8));
        Assert.Equal(127, Wlan.Polarity.Length);
        Assert.Contains("BW", Assert.Throws<WlanRefusal>(() => Wlan.Generate(new WlanSpec(WlanFormat.Vht, 320e6))).Message);
        Assert.Contains("MCS", Assert.Throws<WlanRefusal>(() => Wlan.Generate(new WlanSpec(WlanFormat.Ht, Mcs: 8))).Message);
        Assert.Contains("GI", Assert.Throws<WlanRefusal>(() => Wlan.Generate(new WlanSpec(WlanFormat.He, GuardS: 0.4e-6))).Message);
        Assert.Contains("Window", Assert.Throws<WlanRefusal>(() => Wlan.Generate(new WlanSpec(GuardS: 0.4e-6, WindowS: 0.5e-6))).Message);
    }
}
