// The DSSS family: WCDMA test models and uplink (3GPP TS 25.141 V19.0.0 §6.1.1, TS 25.213 V19.0.0), IEEE 802.11b
// (IEEE Std 802.11-2020 Clauses 15 and 16) and IEEE 802.15.4 2.4 GHz O-QPSK chips (IEEE Std 802.15.4-2020).
//
//   W1  Every test model's codes are orthogonal: no channel's OVSF code lies under another's in the tree.
//   W2  The codes: the downlink scrambling code against a bit-level rewrite of TS 25.213 §5.2.2 (the clause lists no
//       chips), the PSC's aperiodic sidelobes within a quarter of its peak, the SSCs orthogonal.
//   W3  Code-domain power reads back every channel's level within 0.1 dB (TM1 64 DPCH; TM5 0.25 dB: 16QAM's frame
//       power follows its data); a clean record's EVM is 0 and its peak code-domain error far down.
//   W4  PAPR at 0.01 %: TM1 64 DPCH and the uplink reference channel.
//   B1  The Barker code's correlation; the CCK words against Table 109; the CRC-16 worked example and the LENGTH /
//       length-extension examples of Table 98 (IEEE Std 802.11b-1999 clause 18).
//   B2  The long preamble despreads, decodes and descrambles to 1s and the SFD F3A0h; the header's SIGNAL is the rate.
//   B3  Every rate inside the transmit mask, its clean chip EVM 0.
//   Z1  All 16 32-chip sequences against IEEE Std 802.15.4-2003 Table 20.
//   Z2  O-QPSK half-sine: constant envelope, clean chip EVM 0, the main lobe (±1.5 MHz) holds over 99 % of the power.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests.Signals;

public class DsssTests(ITestOutputHelper output)
{
    [Fact]
    public void W1_TestModelCodesAreOrthogonal()
    {
        foreach (var m in Enum.GetValues<WcdmaModel>().Where(m => m != WcdmaModel.Uplink12k2))
        {
            var ch = Wcdma.Channels(m);
            for (int a = 0; a < ch.Length; a++)
                for (int b = a + 1; b < ch.Length; b++)
                {
                    var (s, l) = ch[a].Sf <= ch[b].Sf ? (ch[a], ch[b]) : (ch[b], ch[a]);
                    var cs = Dsss.Ovsf(s.Sf, s.Code); var cl = Dsss.Ovsf(l.Sf, l.Code);
                    for (int seg = 0; seg < l.Sf; seg += s.Sf)
                        Assert.True(Enumerable.Range(0, s.Sf).Sum(j => cs[j] * cl[seg + j]) == 0, $"{m}: {s.Name} and {l.Name}");
                }
        }
    }

    [Fact]
    public void W2_ScramblingAndSynchronisationCodes()
    {
        foreach (int n in new[] { 0, 16, 112 })
        {
            // TS 25.213 §5.2.2 restated on 18-bit words: bit k of the word is x(i + k)
            uint x = 1, y = (1u << 18) - 1;
            for (int i = 0; i < n; i++) x = (x >> 1) | (((x ^ (x >> 7)) & 1) << 17);
            var z = new int[200000];
            for (int i = 0; i < z.Length; i++)
            {
                z[i] = 1 - 2 * (int)((x ^ y) & 1);
                x = (x >> 1) | (((x ^ (x >> 7)) & 1) << 17);
                y = (y >> 1) | (((y ^ (y >> 5) ^ (y >> 7) ^ (y >> 10)) & 1) << 17);
            }
            var s = Wcdma.DownlinkScrambling(n);
            for (int i = 0; i < 64; i++) Assert.Equal(new Complex(z[i], z[i + 131072]), s[i]);
        }

        var psc = Wcdma.Psc();
        Assert.Equal(256, psc.Sum(v => v * v));
        for (int lag = 1; lag < 256; lag++)                             // the hierarchical code's sidelobes stay within a quarter of its peak
            Assert.True(Math.Abs(Enumerable.Range(0, 256 - lag).Sum(i => psc[i] * psc[i + lag])) <= 64, $"PSC lag {lag}");
        for (int k = 1; k <= 16; k++)
            for (int l = k + 1; l <= 16; l++)
                Assert.Equal(0, Wcdma.Ssc(k).Zip(Wcdma.Ssc(l), (a, b) => a * b).Sum());
    }

    [Theory]
    [InlineData(WcdmaModel.Tm1x64, 0.1)]
    [InlineData(WcdmaModel.Tm1x16, 0.1)]
    [InlineData(WcdmaModel.Tm3x32, 0.1)]
    [InlineData(WcdmaModel.Tm5x8, 0.25)]
    public void W3_CodeDomainPowerReadsBackTheLevels(WcdmaModel m, double tol)
    {
        var q = Wcdma.Measure(m, Wcdma.Generate(m));
        var cpich = q.Channels.Single(c => c.Channel.Kind == WcdmaChannelKind.Cpich);
        foreach (var c in q.Channels)
            Assert.True(Math.Abs(c.PowerDb - cpich.PowerDb - (c.Channel.LevelDb - cpich.Channel.LevelDb)) < tol,
                $"{c.Channel.Name}: {c.PowerDb - cpich.PowerDb:0.000} dB to CPICH, set {c.Channel.LevelDb - cpich.Channel.LevelDb} dB");
        Assert.True(q.EvmPercent < 1e-6, $"EVM {q.EvmPercent} %");
        Assert.True(q.PeakCdeDb < -100, $"PCDE {q.PeakCdeDb} dB");
        // the levels sum to the maximum power (TS 25.141 "fraction of power"): CPICH's share of the total is about its level
        Assert.InRange(cpich.PowerDb - cpich.Channel.LevelDb, -0.5, 0.5);
    }

    [Fact]
    public void W4_Papr()
    {
        double tm1 = SignalMeasure.PaprDb(Wcdma.Generate(WcdmaModel.Tm1x64), 1e-4);
        var ul = Wcdma.Generate(WcdmaModel.Uplink12k2);
        double up = SignalMeasure.PaprDb(ul, 1e-4);
        output.WriteLine($"PAPR at 0.01 %: TM1 64 DPCH {tm1:0.00} dB, uplink 12.2 kb/s {up:0.00} dB");
        Assert.InRange(tm1, 9, 12);
        Assert.InRange(up, 2, 6);
        Assert.True(Wcdma.Measure(WcdmaModel.Uplink12k2, ul).EvmPercent < 1e-6);
    }

    [Fact]
    public void B1_BarkerAndCck()
    {
        var b = Ieee80211b.Barker;
        for (int lag = 0; lag < 11; lag++)
        {
            Assert.Equal(lag == 0 ? 11 : -1, Enumerable.Range(0, 11).Sum(i => b[i] * b[(i + lag) % 11]));        // periodic
            Assert.True(Math.Abs(Enumerable.Range(0, 11 - lag).Sum(i => b[i] * b[i + lag])) <= (lag == 0 ? 11 : 1)); // aperiodic
        }
        Assert.Equal(new Complex[] { 1, 1, 1, -1, 1, 1, -1, 1 }, Ieee80211b.Cck(0, 0, 0, 0).Select(c => new Complex(Math.Round(c.Real), Math.Round(c.Imaginary))));

        // Table 109, the 5.5 Mb/s words (φ2 = d2 π + π/2, φ3 = 0, φ4 = d3 π), c0 first
        var j = Complex.ImaginaryOne;
        foreach (var (d2, d3, word) in new[]
        {
            (0, 0, new[] { j, 1, j, -1, j, 1, -j, 1 }), (0, 1, new[] { -j, -1, -j, 1, j, 1, -j, 1 }),
            (1, 0, new[] { -j, 1, -j, -1, -j, 1, j, 1 }), (1, 1, new[] { j, -1, j, 1, -j, 1, j, 1 }),
        })
            Assert.Equal(word, Ieee80211b.Cck(0, d2 * Math.PI + Math.PI / 2, 0, d3 * Math.PI).Select(c => new Complex(Math.Round(c.Real), Math.Round(c.Imaginary))));

        // the CRC-16 worked example: SIGNAL 0Ah, SERVICE 0, LENGTH 192 µs
        Assert.Equal("0101101101010111", string.Concat(Ieee80211b.Crc16("01010000000000000000001100000000".Select(c => c - '0'))));
        // Table 98 at 11 Mb/s: octets -> LENGTH (µs), length extension
        foreach (var (octets, length, ext) in new[] { (1023, 744, 0), (1024, 745, 0), (1025, 746, 0), (1026, 747, 1) })
        {
            var h = Ieee80211b.Header(new Wlan11bShape(Wlan11bRate.Cck11M, octets));
            Assert.Equal((length, ext), (Enumerable.Range(0, 16).Sum(i => h[16 + i] << i), h[15]));
        }
    }

    [Theory]
    [InlineData(Wlan11bRate.Dbpsk1M, 0x0A)]
    [InlineData(Wlan11bRate.Cck11M, 0x6E)]
    public void B2_PreambleAndHeaderDecode(Wlan11bRate rate, int signal)
    {
        var chips = Ieee80211b.Chips(new Wlan11bShape(rate, 50));
        var sym = Enumerable.Range(0, 192).Select(k => Enumerable.Range(0, 11).Aggregate(Complex.Zero, (a, j) => a + chips[11 * k + j] * Ieee80211b.Barker[j])).ToArray();
        var s = Enumerable.Range(0, 192).Select(k => (sym[k] * Complex.Conjugate(k == 0 ? 11 : sym[k - 1])).Real < 0 ? 1 : 0).ToArray();
        var d = Enumerable.Range(7, 185).Select(i => s[i] ^ s[i - 4] ^ s[i - 7]).ToArray();   // bit i + 7
        Assert.All(d.Take(128 - 7), v => Assert.Equal(1, v));
        Assert.Equal(0xF3A0, Enumerable.Range(0, 16).Sum(i => d[121 + i] << i));
        Assert.Equal(signal, Enumerable.Range(0, 8).Sum(i => d[137 + i] << i));
    }

    [Theory]
    [InlineData(Wlan11bRate.Dbpsk1M)]
    [InlineData(Wlan11bRate.Dqpsk2M)]
    [InlineData(Wlan11bRate.Cck5M5)]
    [InlineData(Wlan11bRate.Cck11M)]
    public void B3_InsideTheMask_CleanEvmZero(Wlan11bRate rate)
    {
        var s = new Wlan11bShape(rate, 100);
        var x = Ieee80211b.Generate(s);
        Assert.Equal(s.Chips * Ieee80211b.SamplesPerChip, x.Length);
        Assert.True(Ieee80211b.MaskMarginDb(x, Ieee80211b.NativeRateHz) > 0);
        var (rms, peak) = Ieee80211b.Evm(s, x);
        Assert.True(rms < 1e-6 && peak < 1e-6, $"EVM {rms} % rms, {peak} % peak");
    }

    [Fact]
    public void Z1_ChipSequencesAgainstTheTable()
    {
        string[] table =
        [
            "11011001110000110101001000101110", "11101101100111000011010100100010", "00101110110110011100001101010010",
            "00100010111011011001110000110101", "01010010001011101101100111000011", "00110101001000101110110110011100",
            "11000011010100100010111011011001", "10011100001101010010001011101101", "10001100100101100000011101111011",
            "10111000110010010110000001110111", "01111011100011001001011000000111", "01110111101110001100100101100000",
            "00000111011110111000110010010110", "01100000011101111011100011001001", "10010110000001110111101110001100",
            "11001001011000000111011110111000",
        ];
        for (int sym = 0; sym < 16; sym++) Assert.Equal(table[sym], string.Concat(Ieee802154.ChipSequence(sym)));
        // the SFD A7h goes out low nibble first: bits b0..b7 = 11100101 (Figure 17)
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 7, 10], Ieee802154.SymbolsOf(new Oqpsk154Shape(1)).Take(10));
    }

    [Fact]
    public void Z2_HalfSineOqpsk_ConstantEnvelope_CleanEvm_MainLobe()
    {
        var s = new Oqpsk154Shape(127);
        var x = Ieee802154.Generate(s);
        Assert.Equal(s.Symbols * 32 * Ieee802154.SamplesPerChip, x.Length);
        Assert.All(x, v => Assert.Equal(1, v.Magnitude, 12));
        var (rms, _) = Ieee802154.Evm(s, x);
        Assert.True(rms < 1e-9);

        var X = Fft.Forward(x);
        double total = X.Sum(v => v.Magnitude * v.Magnitude), inLobe = 0;
        for (int i = 0; i < X.Length; i++)
            if (Math.Abs((i <= X.Length / 2 ? i : i - X.Length) * Ieee802154.NativeRateHz / X.Length) <= 1.5e6) inLobe += X[i].Magnitude * X[i].Magnitude;
        Assert.True(inLobe / total > 0.99, $"{100 * inLobe / total:0.00} %");
    }
}
