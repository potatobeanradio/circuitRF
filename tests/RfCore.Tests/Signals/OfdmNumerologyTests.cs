// Uplink DFT-s-OFDM / SC-FDMA, the 3GPP test models with TDD, and the OFDM numerology presets.
//
//   O1  With the new options unset the LTE / NR record is what it was: stored samples, power and sums to 1e-9 relative
//       (taken from the generator before the options existed).
//   O2  NR uplink DFT-s-OFDM (TS 38.211 V19.5.0 cl. 6.3.1.4), 50 RB at 30 kHz, PAPR at 0.01 % on the 4x-interpolated
//       record: QPSK at least 2 dB under CP-OFDM QPSK on the same allocation (9.5 against 6.6 dB), pi/2-BPSK (cl. 5.1.1,
//       no spectrum shaping) at least 0.5 dB under DFT-s QPSK (5.8 dB); LTE SC-FDMA (TS 36.211 V19.3.0 cl. 5.3.3) at
//       least 2 dB under the LTE downlink (6.7 against 9.2 dB).
//   O3  pi/2-BPSK: even-indexed points on +-(1+j)/sqrt2, odd-indexed rotated by pi/2, and the EVM path (FFT, equaliser,
//       inverse DFT) returns them to 1e-9.
//   O4  Test models read back per RB from the record within 0.1 dB: NR-FR1-TM1.2 (TS 38.141-1 V19.6.0 table 4.9.2.2.2-1)
//       at 20 MHz / 30 kHz boosts RBGs 1, 3, 5, 11 (P = 4) by 3 dB and deboosts the rest by 10 log10((48 - 2 x 16)/32);
//       E-TM1.2 (TS 36.141 V19.1.0 table 6.1.1.2-1/-2) at 10 MHz in subframe 1 holds its 20 listed PRBs at PA +3 dB and
//       the other 30 at -4.73 dB, against the CRS.
//   O5  TDD gating: NR-FR1-TM1.1 at 30 kHz with the test model's DDDDDDDSUU (6 DL symbols in S) transmits 104 of 140
//       symbols, nothing in the others; a TDD E-TM (configuration 3, special subframe 8) 95 of 140.
//   O6  DVB-T 8K (ETSI EN 300 744 V1.6.2 cl. 4.4, 4.5): 6817 carriers at 64/7 MHz / 8192 = 1116 Hz, 177 continual pilots
//       and 68 TPS carriers, scattered pilots at k = 3 (l mod 4) + 12 p, all pilots at +-4/3 from the PRBS that starts
//       1111111111100.
//   O7  DAB mode I (ETSI EN 300 401 V2.2.1 cl. 14): 1536 carriers at 1 kHz, a silent null symbol, 196608 samples (96 ms),
//       and every data carrier a pi/4-DQPSK step from the symbol before (pi/4 + m pi/2, to 1e-9 off the record).
//   O8  NB-IoT uplink (TS 36.211 cl. 10.1): a 3.75 kHz single tone sits at (k + 1/2) 3.75 kHz in a 2 ms slot of 3840
//       samples; 15 kHz multi-tone carriers sit 15 kHz apart within 180 kHz.
//   O9  Clean EVM below -50 dB, pilot-aided, for every preset and new carrier kind (and NR FR2-2 at 960 kHz through the
//       existing SignalEvm path, TS 38.101-2 V19.5.0 table 5.3.2-1: 400 MHz = 33 RB).

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;

namespace RfCore.Tests.Signals;

public class OfdmNumerologyTests
{
    private static void Near(Complex want, Complex got, string what) =>
        Assert.True((got - want).Magnitude <= 1e-9 * Math.Max(want.Magnitude, 1e-300), $"{what}: {got}, was {want}");

    private static readonly (CarrierSpec C, int HalfMs, double W, double? Shape, (int I, Complex X)[] At, double P, Complex Sum, Complex IdealSum)[] Before =
    [
        (new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6, Modulation = QamOrder.Qam64, Seed = 1 }, 1, 0.2, -80,
         [(0, new(-0.0010530784664998564, -0.6022155215490347)), (2194, new(0.07488825050836051, -1.0125952519170052)),
          (5120, new(-0.8619485644192172, 0.25411672875932323)), (7680, new(0.5786676824013582, 0.4732082266807788)),
          (15359, new(-1.436338265562565, -0.31455526578206927))],
         1.0000000000000047, new(-30.37379983962943, -3.01022758295311), new(-38.884444190447155, -75.30003478150083)),
        (new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 1.4e6, Modulation = QamOrder.Qpsk, Seed = 7 }, 2, 0.2, null,
         [(0, new(-0.20779621025610923, 0.12270999995641986)), (274, new(-2.398812378960631, -0.7728733403563425)),
          (640, new(0.18617342123955916, 0.5621567100410787)), (960, new(-0.8808617810425003, -0.25025511125651695)),
          (1919, new(-0.6085143358515614, 0.4019939439828374))],
         1.000000000000003, new(-17.95899524910831, -6.8185982531970915), new(-42.42640687119281, -7.071067811865474)),
        (new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qam256, Seed = 3 }, 1, 0.2, null,
         [(0, new(0.04636450316255219, 0.12962837482302328)), (2194, new(0.27497697570128565, 0.1694489316266519)),
          (5120, new(0.9793273312335783, -0.8413592246580955)), (7680, new(0.20759647494432976, -1.1641637306946577)),
          (15359, new(0.35478999234292213, 0.5026782118321184))],
         1.0000000000000004, new(-277.6732071669239, 149.20584683335284), new(17.640194743489374, -30.83199255166402)),
        (new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 100e6, ScsHz = 120e3, Modulation = QamOrder.Qam16, Seed = 2 }, 1, 0.0, -80,
         [(0, new(-0.20926849150909663, -0.06825657874553272)), (8777, new(0.1557424546041571, 0.9282086273539795)),
          (20480, new(-0.3107423398242357, -0.7199900594387179)), (30720, new(-0.2837367725151652, -0.37019929236534793)),
          (61439, new(1.316293625587425, 0.3524159106533056))],
         0.9999999999999946, new(218.59511376225626, -74.93539870028502), new(-71.46747511980504, 17.70875489694299)),
    ];

    [Fact]
    public void O1_UnsetOptionsLeaveLteAndNrAsTheyWere()
    {
        foreach (var (c, h, w, shape, at, p, sum, idealSum) in Before)
            foreach (var x in new[] { OfdmGenerator.Generate(c, h, w, shape), OfdmGenerator.Generate(c, h, w, shape, new OfdmOptions()) })
            {
                Assert.Equal(at[^1].I + 1, x.Length);
                foreach (var (i, v) in at) Near(v, x[i], $"{c.Label} sample {i}");
                Near(p, x.Average(v => v.Real * v.Real + v.Imaginary * v.Imaginary), $"{c.Label} mean power");
                Near(sum, x.Aggregate(Complex.Zero, (a, v) => a + v), $"{c.Label} sum");
                Near(idealSum, OfdmGenerator.IdealSymbols(c, h).SelectMany(r => r).Aggregate(Complex.Zero, (a, v) => a + v), $"{c.Label} ideal sum");
            }
    }

    private static double Papr(Complex[] x) => SignalMeasure.PaprDb(Fft.ResamplePeriodic(x, 4 * x.Length), 1e-4);

    private static readonly CarrierSpec Nr20 = new() { Standard = CarrierStandard.Nr, BandwidthHz = 20e6, ScsHz = 30e3, Modulation = QamOrder.Qpsk, Seed = 5 };
    private static readonly CarrierSpec Lte10 = new() { Standard = CarrierStandard.Lte, BandwidthHz = 10e6, Modulation = QamOrder.Qpsk, Seed = 5 };

    [Fact]
    public void O2_DftSpreadingLowersThePapr()
    {
        var ul = new OfdmOptions { Uplink = true, RbStart = 0, RbCount = 50 };
        double cp = Papr(OfdmGenerator.Generate(Nr20, 4, 0.2, null, ul));
        double dft = Papr(OfdmGenerator.Generate(Nr20, 4, 0.2, null, ul with { TransformPrecoding = true }));
        double bpsk = Papr(OfdmGenerator.Generate(Nr20, 4, 0.2, null, ul with { TransformPrecoding = true, Pi2Bpsk = true }));
        double lteDl = Papr(OfdmGenerator.Generate(Lte10, 4, 0.2, null));
        double lteUl = Papr(OfdmGenerator.Generate(Lte10, 4, 0.2, null, new OfdmOptions { Uplink = true, RbCount = 48 }));
        string all = $"CP-OFDM {cp:0.00}, DFT-s QPSK {dft:0.00}, pi/2-BPSK {bpsk:0.00}, LTE DL {lteDl:0.00}, SC-FDMA {lteUl:0.00} dB";
        Assert.True(cp - dft >= 2, all);
        Assert.True(dft - bpsk >= 0.5, all);
        Assert.True(lteDl - lteUl >= 2, all);
        Assert.Throws<ArgumentException>(() => Ofdm3gpp.Grid(Nr20, ul with { TransformPrecoding = true, RbCount = 51 }, 1));  // 51 = 3 x 17
    }

    [Fact]
    public void O3_Pi2Bpsk_RotatesEveryOtherPoint()
    {
        var o = new OfdmOptions { Uplink = true, TransformPrecoding = true, Pi2Bpsk = true, RbStart = 3, RbCount = 10 };
        var g = Ofdm3gpp.Grid(Nr20, o, 1);
        double a = 1 / Math.Sqrt(2);
        foreach (var pre in g.Pre!.Where(p => p is not null))
            for (int i = 0; i < pre!.Length; i++)
            {
                var axis = i % 2 == 0 ? new Complex(a, a) : new Complex(-a, a);
                Assert.True(Math.Min((pre[i] - axis).Magnitude, (pre[i] + axis).Magnitude) < 1e-12, $"point {i}: {pre[i]}");
            }
        var r = OfdmEvm.Measure(g, OfdmGenerator.Generate(Nr20, 1, 0.2, null, o));
        Assert.True(r.PilotAided);
        for (int i = 0; i < r.Points; i++) Assert.True((r.Received[i] - r.Ideal[i]).Magnitude < 1e-9, $"point {i}");
    }

    /// <summary>Mean |X|^2 per RB of the received grid over the symbols of <paramref name="symbols"/>, the columns
    /// <paramref name="use"/> says.</summary>
    private static double[] RbPowerDb(OfdmGrid g, Complex[] rec, int nRb, Func<int, bool> symbols, Func<int, int, bool> use, double reference)
    {
        var y = g.Demodulate(rec);
        var p = new double[nRb];
        var n = new int[nRb];
        for (int s = 0; s < g.Symbols; s++)
        {
            if (y[s] is not { } row || !symbols(s)) continue;
            for (int k = 0; k < row.Length; k++)
                if (use(s, k)) { p[k / 12] += row[k].Real * row[k].Real + row[k].Imaginary * row[k].Imaginary; n[k / 12]++; }
        }
        return p.Select((v, i) => n[i] > 0 ? 10 * Math.Log10(v / n[i] / reference) : double.NaN).ToArray();
    }

    [Fact]
    public void O4_TestModels_HoldTheirLevelsPerRb()
    {
        // NR-FR1-TM1.2, 20 MHz at 30 kHz: 51 RB, BWP from RB 3 of 48 RB, P = 4, N_RBG = min(floor(0.4 x 48 / 4), (11 - 1)/2 + 1) = 4
        var nr = Nr20 with { Modulation = QamOrder.Qam64 };
        var o = new OfdmOptions { TestModel = "NR-FR1-TM1.2" };
        var g = Ofdm3gpp.Grid(nr, o, 1);
        var rec = OfdmGenerator.Generate(nr, 1, 0.2, null, o);
        var y = g.Demodulate(rec);
        double refP = Enumerable.Range(0, 36).Average(k => y[5]![k].Magnitude * y[5]![k].Magnitude);   // RB 0-2, 0 dB QPSK
        var db = RbPowerDb(g, rec, 51, s => s % 14 >= 3, (s, k) => g.Role[s][k] == ReRole.Data, refP);
        int[] boosted = [.. Enumerable.Range(4, 4), .. Enumerable.Range(12, 4), .. Enumerable.Range(20, 4), .. Enumerable.Range(44, 4)];
        double deboost = 10 * Math.Log10((48 - Math.Pow(10, 0.3) * 16) / 32);
        for (int rb = 3; rb < 51; rb++)
            Assert.True(Math.Abs(db[rb] - (boosted.Contains(rb) ? 3 : deboost)) < 0.1, $"NR RB {rb}: {db[rb]:0.000} dB");
        Assert.True(Math.Abs(db[0]) < 0.1 && Math.Abs(db[2]) < 0.1);

        // E-TM1.2 at 10 MHz, subframe 1 (no PBCH or sync): table 6.1.1.2-2 lists its 20 boosted PRBs
        var lte = new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 10e6, Seed = 2 };
        var oe = new OfdmOptions { TestModel = "E-TM1.2" };
        var ge = Ofdm3gpp.Grid(lte, oe, 4);
        var re = OfdmGenerator.Generate(lte, 4, 0.2, null, oe);
        var ye = ge.Demodulate(re);
        double crs = Enumerable.Range(0, 600).Where(k => ge.Role[14][k] == ReRole.Pilot).Average(k => ye[14]![k].Magnitude * ye[14]![k].Magnitude);
        var dbe = RbPowerDb(ge, re, 50, s => s / 14 == 1, (s, k) => ge.Role[s][k] == ReRole.Data, crs);
        int[] listed = [5, 6, 7, 9, 11, 15, 20, 21, 22, 24, 25, 27, 34, 35, 36, 37, 40, 44, 46, 49];
        for (int rb = 0; rb < 50; rb++)
            Assert.True(Math.Abs(dbe[rb] - (listed.Contains(rb) ? 3 : -4.73)) < 0.1, $"E-TM RB {rb}: {dbe[rb]:0.000} dB");
    }

    [Fact]
    public void O5_TddGatesTheRecord()
    {
        var nr = Nr20 with { Modulation = QamOrder.Qam64 };
        var o = new OfdmOptions { TestModel = "NR-FR1-TM1.1", Tdd = "TM" };
        var g = Ofdm3gpp.Grid(nr, o, 10);                                     // 5 ms: one TDD period
        Assert.Equal(104, g.On.Count(v => v));
        var rec = OfdmGenerator.Generate(nr, 10, 0.2, null, o);
        var starts = g.SymbolStarts();
        for (int s = 0; s < g.Symbols; s++)
        {
            double e = Enumerable.Range(starts[s] + g.Cp[s], g.Nfft).Sum(i => rec[i].Magnitude * rec[i].Magnitude);
            Assert.True(g.On[s] ? e > 1 : e == 0, $"symbol {s} ({(g.On[s] ? "on" : "off")}): {e}");
        }
        Assert.InRange(g.Duty, 104.0 / 140 - 1e-3, 104.0 / 140 + 1e-3);

        var lte = new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 5e6 };
        var ge = Ofdm3gpp.Grid(lte, new OfdmOptions { TestModel = "E-TM3.1", Tdd = "TM" }, 20);
        Assert.Equal(95, ge.On.Count(v => v));
        Assert.Throws<ArgumentException>(() => Ofdm3gpp.Grid(lte, new OfdmOptions { TestModel = "E-TM3.1", Tdd = "DDDSU:10,2" }, 2));
    }

    [Fact]
    public void O6_DvbT8K_CarriersAndPilots()
    {
        var nm = OfdmPresets.DvbT8K;
        var g = nm.Build(4, 1);
        Assert.Equal(6817, g.Bins.Length);
        Assert.Equal(64e6 / 7 / 8192, g.ColumnHz(1) - g.ColumnHz(0), 6);
        Assert.Equal(0.0, g.ColumnHz(3408), 9);
        Assert.Equal(177, nm.ContinualPilots.Length);
        Assert.Equal(68, nm.SignallingCarriers.Length);
        Assert.Equal(45, OfdmPresets.DvbT2K.ContinualPilots.Length);
        Assert.Equal(17, OfdmPresets.DvbT2K.SignallingCarriers.Length);
        var w = OfdmNumerology.Prbs(PilotSequence.Dvb, 6817);
        Assert.Equal("1111111111100", string.Concat(w.Take(13)));
        for (int l = 0; l < 4; l++)
            for (int k = 0; k < 6817; k++)
            {
                bool scattered = k % 12 == 3 * l, continual = nm.ContinualPilots.Contains(k);
                if (scattered || continual)
                {
                    Assert.Equal(ReRole.Pilot, g.Role[l][k]);
                    Assert.Equal(4.0 / 3 * 2 * (0.5 - w[k]), g.X[l][k].Real, 12);
                    Assert.Equal(0, g.X[l][k].Imaginary);
                }
                else Assert.NotEqual(ReRole.Pilot, g.Role[l][k]);
            }
        Assert.Equal(nm.ContinualPilots.Concat(Enumerable.Range(0, 569).Select(p => 12 * p)).Distinct().Count(),
                     g.Role[0].Count(r => r == ReRole.Pilot));
    }

    [Fact]
    public void O7_DabModeI_CarriersAndDqpsk()
    {
        var g = OfdmPresets.DabModeI.Build(77, 3);
        var rec = g.Synthesize(0.2);
        Assert.Equal(196608, rec.Length);
        Assert.Equal(1536, g.Bins.Length);
        Assert.Equal(1000, g.ColumnHz(768) - g.ColumnHz(767) - 1000, 9);   // the unused DC carrier between them
        Assert.Equal(1000, g.ColumnHz(1) - g.ColumnHz(0), 9);
        Assert.False(g.On[0]);
        Assert.Equal(0, Enumerable.Range(120, 2656 - 120).Sum(i => rec[i].Magnitude));   // null symbol, past the last symbol's window tail
        var y = g.Demodulate(rec);
        for (int s = 2; s < 77; s++)
            for (int k = 0; k < 1536; k += 37)
            {
                double ph = (y[s]![k] / y[s - 1]![k]).Phase;
                double m = (ph - Math.PI / 4) / (Math.PI / 2);
                Assert.True(Math.Abs(m - Math.Round(m)) < 1e-9, $"symbol {s} carrier {k}: {ph}");
            }
    }

    [Fact]
    public void O8_NbIot_ToneSpacing()
    {
        var g = Ofdm3gpp.NbIotUplink(3.75e3, 1, 30, qpsk: false, slots: 2, seed: 1);
        var rec = g.Synthesize(0);
        Assert.Equal(2 * 3840, rec.Length);
        double want = (30 - 24 + 0.5) * 3.75e3;
        Assert.Equal(want, g.ColumnHz(30), 9);
        var spec = Fft.Forward(rec);
        int peak = Enumerable.Range(0, spec.Length).MaxBy(i => spec[i].Magnitude);
        double df = 1.92e6 / spec.Length, f = (peak < spec.Length / 2 ? peak : peak - spec.Length) * df;
        Assert.True(Math.Abs(f - want) <= 3.75e3 / 2, $"tone at {f} Hz, want {want}");
        var mt = Ofdm3gpp.NbIotUplink(15e3, 12, 0, qpsk: true, slots: 2, seed: 1);
        for (int k = 1; k < 12; k++) Assert.Equal(15e3, mt.ColumnHz(k) - mt.ColumnHz(k - 1), 9);
        Assert.Equal(-82.5e3, mt.ColumnHz(0), 9);
        Assert.Equal(82.5e3, mt.ColumnHz(11), 9);
    }

    public static TheoryData<string> Kinds() =>
    [
        "DVB-T 2K", "DVB-T 8K", "DVB-T2 8K PP2", "ISDB-T mode 3", "DAB mode I", "WiMAX 10 MHz PUSC",
        "NR UL DFT-s", "NR UL pi/2", "NR UL CP-OFDM", "LTE UL", "LTE-M", "NR-FR1-TM3.1", "NR-FR1-TM2", "E-TM3.1", "E-TM3.2 TDD",
        "NB 3.75k", "NB 15k BPSK", "NB 3-tone", "NB 6-tone", "NB 12-tone",
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void O9_CleanEvm_IsBelowMinus50Db(string kind)
    {
        var lte5 = new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 5e6, Modulation = QamOrder.Qam64 };
        (OfdmGrid g, Complex[] rec) Of(CarrierSpec c, OfdmOptions o, int h) => (Ofdm3gpp.Grid(c, o, h), OfdmGenerator.Generate(c, h, 0.2, null, o));
        (OfdmGrid g, Complex[] rec) Grid(OfdmGrid g) => (g, g.Synthesize(0.2));
        var nm = OfdmPresets.All.FirstOrDefault(p => p.Name == kind);
        var (g, rec) = nm is not null ? Grid(nm.Build(nm.DabDifferential ? 77 : 4, 2)) : kind switch
        {
            "NR UL DFT-s" => Of(Nr20 with { Modulation = QamOrder.Qam256 }, new() { Uplink = true, TransformPrecoding = true, RbStart = 1, RbCount = 25 }, 2),
            "NR UL pi/2" => Of(Nr20, new() { Uplink = true, TransformPrecoding = true, Pi2Bpsk = true, RbCount = 6 }, 2),
            "NR UL CP-OFDM" => Of(Nr20 with { Modulation = QamOrder.Qam64 }, new() { Uplink = true, RbStart = 7, RbCount = 20 }, 2),
            "LTE UL" => Of(lte5 with { Modulation = QamOrder.Qam16 }, new() { Uplink = true, RbStart = 2, RbCount = 12 }, 2),
            "LTE-M" => Of(lte5 with { Modulation = QamOrder.Qpsk }, Ofdm3gpp.LteM(25, 1), 2),
            "NR-FR1-TM3.1" => Of(Nr20 with { Modulation = QamOrder.Qam64 }, new() { TestModel = "NR-FR1-TM3.1" }, 2),
            "NR-FR1-TM2" => Of(Nr20 with { Modulation = QamOrder.Qam64 }, new() { TestModel = "NR-FR1-TM2" }, 2),
            "E-TM3.1" => Of(lte5, new() { TestModel = "E-TM3.1" }, 4),
            "E-TM3.2 TDD" => Of(lte5, new() { TestModel = "E-TM3.2", Tdd = "TM" }, 20),
            "NB 3.75k" => Grid(Ofdm3gpp.NbIotUplink(3.75e3, 1, 5, qpsk: true, slots: 2, seed: 1)),
            "NB 15k BPSK" => Grid(Ofdm3gpp.NbIotUplink(15e3, 1, 11, qpsk: false, slots: 4, seed: 1)),
            "NB 3-tone" => Grid(Ofdm3gpp.NbIotUplink(15e3, 3, 9, qpsk: true, slots: 4, seed: 1)),
            "NB 6-tone" => Grid(Ofdm3gpp.NbIotUplink(15e3, 6, 6, qpsk: true, slots: 4, seed: 1)),
            "NB 12-tone" => Grid(Ofdm3gpp.NbIotUplink(15e3, 12, 0, qpsk: true, slots: 4, seed: 1)),
            _ => throw new ArgumentException(kind),
        };
        var r = OfdmEvm.Measure(g, rec);
        Assert.True(r.PilotAided, $"{kind}: no pilots");
        Assert.True(r.Points > 0);
        Assert.True(r.RmsDb < -50, $"{kind}: EVM {r.RmsDb:0.0} dB");
    }

    [Fact]
    public void O9_Fr2_2_At960kHz_MeasuresThroughTheExistingPath()
    {
        var c = new CarrierSpec { Standard = CarrierStandard.Nr, BandwidthHz = 400e6, ScsHz = 960e3, Modulation = QamOrder.Qam64 };
        var nm = OfdmGenerator.Numerology(c);
        Assert.Equal(33, nm.NRb);
        Assert.Equal(512, nm.Nfft);
        Assert.Equal(7 << 6, nm.SymbolsPerHalfMs);
        var x = OfdmGenerator.Generate(c, 1, 0.2, null);
        Assert.True(SignalEvm.Measure(c, x, 0.5e-3).RmsDb < -50);
        Assert.Equal(148, OfdmGenerator.Numerology(c with { BandwidthHz = 2000e6 }).NRb);
        Assert.Equal(248, OfdmGenerator.Numerology(c with { ScsHz = 480e3, BandwidthHz = 1600e6 }).NRb);
    }
}
