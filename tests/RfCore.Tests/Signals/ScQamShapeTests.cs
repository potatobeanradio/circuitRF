// The single carrier's pulse shapes, rotated, differential and offset constellations, SOQPSK-TG and bursts.
//
//   V0  Options unset: the shape and its instants are as before (the bytes were compared against the previous
//       generator when the options went in).
//   V1  The points turn and chain per symbol: π/2-BPSK, π/4-QPSK, 3π/8-8PSK, π/4-DQPSK, D8PSK.
//   V2  Each pulse's spectrum against its formula (first nulls, the Gaussian's 3 dB point, RC's half point), and the
//       generated record's 99 % power bandwidth against BandwidthHz (SOQPSK-TG: 0.78 times the bit rate, IRIG 106).
//   V3  OQPSK's PAPR below QPSK's and its envelope clear of zero (RRC 0.35); half-sine OQPSK and SOQPSK-TG of constant envelope.
//   V4  EDGE's PAPR (3GPP TS 45.004 8PSK), against the ~3.2 dB usually quoted.
//   V5  Bursts: whole bursts in the record, silence in the guard, raised-cosine ramps.
//   V6  Every preset and variant reads a clean EVM below −50 dB through its own receiver.
//   V7  SOQPSK-TG's precoder: the OQPSK decisions on the waveform are the bits sent.

using System;
using System.Linq;
using System.Numerics;
using RfCore.Signals;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests.Signals;

public class ScQamShapeTests(ITestOutputHelper output)
{
    private static ScShape Sc(string mod, double rollOff = 0.35, double rs = 1e6)
        => ScQam.Resolve(new CarrierSpec { Standard = CarrierStandard.Sc, SymbolRateHz = rs, ScModulation = ScQam.ParseMod(mod), RollOff = rollOff });

    private static ScShape With(ScShape s, Func<ScShape, ScShape> f) => ScQam.Check(ScQam.Close(f(s)));

    private static double PaprDb(Complex[] x)
        => 10 * Math.Log10(x.Max(v => v.Magnitude * v.Magnitude) / x.Average(v => v.Magnitude * v.Magnitude));

    /// <summary>The clean record through the carrier's receiver at the data instants, one gain, against the ideal points
    /// read at unit mean power — the SC EVM's arithmetic.</summary>
    private static double EvmDb(ScShape s)
    {
        var mf = ScQam.Filter(s, ScQam.Generate(s), ScQam.NativeRateHz(s), matched: true);
        var sy = ScQam.Symbols(s);
        var d = Enumerable.Range(0, sy.Points.Length).Where(i => !sy.Known[i]).ToArray();
        Complex num = 0; double den = 0;
        foreach (int i in d) { num += mf[sy.At[i]] * Complex.Conjugate(sy.Points[i]); den += sy.Points[i].Magnitude * sy.Points[i].Magnitude; }
        var g = num / den;
        double err = d.Sum(i => Math.Pow((mf[sy.At[i]] / g - sy.Points[i]).Magnitude, 2));
        return 10 * Math.Log10(err / d.Length);
    }

    /// <summary>The bandwidth holding 99 % of the record's power, bins taken outwards from the centre.</summary>
    private static double Occupied99(Complex[] x, double fs)
    {
        var spec = Fft.Forward(x);
        int n = spec.Length;
        var bins = Enumerable.Range(0, n).Select(k => (f: Math.Abs((k <= n / 2 ? k : k - n) * fs / n), p: spec[k].Magnitude * spec[k].Magnitude))
                             .OrderBy(b => b.f).ToArray();
        double total = bins.Sum(b => b.p), acc = 0;
        foreach (var b in bins) if ((acc += b.p) >= 0.99 * total) return 2 * b.f;
        return fs;
    }

    [Fact]
    public void V0_OptionsUnset_TheShapeIsAsBefore()
    {
        var c = SignalSpec.ParseCarrier("SC;Rs=1Msps;Mod=16APSK;RollOff=0.2;Data=PN9;Preamble=32;PilotBlock=8/100;Off=0;P=0");
        var s = ScQam.Resolve(c);
        Assert.Equal(s.DataSymbols, ScQam.Close(s).DataSymbols);
        Assert.False(ScQam.IsReference(s));
        Assert.Equal(s.RecordSymbols * ScQam.SamplesPerSymbol, s.Samples);
        Assert.Equal(s.RecordSymbols / s.SymbolRate, s.RecordSeconds);
        Assert.Equal(Enumerable.Range(0, s.RecordSymbols).Select(k => k * ScQam.SamplesPerSymbol), ScQam.Symbols(s).At);
        Assert.Equal(1.2e6, ScQam.BandwidthHz(s), 1e-6);
    }

    [Fact]
    public void V1_ThePointsTurnAndChainPerSymbol()
    {
        static void Rotated(ScShape s, double deg)
        {
            var sy = ScQam.Symbols(s); var cons = ScQam.Constellation(s);
            for (int k = 0; k < sy.Points.Length; k++)
                if (!sy.Known[k])
                    Assert.True((sy.Points[k] * Complex.FromPolarCoordinates(1, -k * deg * Math.PI / 180) - cons[sy.Labels[k]]).Magnitude < 1e-9);
            Assert.Equal(0, s.RecordSymbols * deg / 360 % 1, 9);   // the record closes
        }
        Rotated(With(Sc("BPSK"), s => s with { RotationDeg = 90 }), 90);
        Rotated(With(Sc("QPSK"), s => s with { RotationDeg = 45 }), 45);
        Rotated(With(Sc("8PSK"), s => s with { RotationDeg = 67.5 }), 67.5);

        static void Chained(ScShape s, double[] stepsDeg)
        {
            var sy = ScQam.Symbols(s); var cons = ScQam.Constellation(s);
            int n = sy.Points.Length;
            for (int k = 0; k < n; k++)
            {
                var step = sy.Points[k] / sy.Points[(k + n - 1) % n];   // across the record's wrap too
                double deg = Math.Atan2(step.Imaginary, step.Real) * 180 / Math.PI;
                Assert.Contains(stepsDeg, a => Math.Abs(Math.IEEERemainder(deg - a, 360)) < 1e-6);
                Assert.True((step - cons[sy.Labels[k]]).Magnitude < 1e-9);
            }
        }
        Chained(With(Sc("QPSK"), s => s with { Differential = true }), [45, 135, -135, -45]);
        Chained(With(Sc("8PSK"), s => s with { Differential = true }), [0, 45, 90, 135, 180, -135, -90, -45]);
    }

    [Fact]
    public void V2_EachPulsesSpectrum_MatchesItsFormula()
    {
        var q = Sc("QPSK");
        double rs = q.SymbolRate;
        Assert.True(ScQam.Response(q with { Pulse = ScPulse.Rect }, rs).Magnitude < 1e-12);            // sinc: first null at Rs
        Assert.True(ScQam.Response(q with { Pulse = ScPulse.HalfSine }, 1.5 * rs).Magnitude < 1e-12);  // cos pulse: at 1.5 Rs
        var gauss = q with { Pulse = ScPulse.Gaussian, Bt = 0.5 };
        Assert.Equal(Math.Sin(Math.PI * 0.5) / (Math.PI * 0.5) / Math.Sqrt(2), ScQam.Response(gauss, 0.5 * rs).Real, 12);
        var rc = q with { Pulse = ScPulse.Rc };
        Assert.Equal(0.5, ScQam.Response(rc, rs / 2).Real, 12);
        Assert.Equal(0, ScQam.Response(rc, rs * 1.35 / 2).Real, 12);
        Assert.Equal(ScQam.LinearGmsk(1.7), ScQam.LinearGmsk(3.3), 3);   // c0 is symmetric, but for g's tails cut at 0 and 4

        foreach (var s in new[] { gauss, q with { Pulse = ScPulse.Rect }, q with { Pulse = ScPulse.HalfSine, Offset = true },
                                  ScQam.Close(ScQamPresets.All[0].Shape with { Guard = 0, Ramp = 0 }), Sc("SOQPSK-TG") })
        {
            double measured = Occupied99(ScQam.Generate(s), ScQam.NativeRateHz(s)), stated = ScQam.BandwidthHz(s);
            output.WriteLine($"{ScQam.PulseName(s.Pulse)} {ScQam.ModName(s.Mod)}: 99 % bandwidth {measured / s.SymbolRate:F3} Rs, stated {stated / s.SymbolRate:F3} Rs");
            Assert.InRange(measured / stated, 0.95, 1.05);
        }
    }

    [Fact]
    public void V3_Oqpsk_HasThePaprAdvantage()
    {
        var q = ScQam.Resolve(new CarrierSpec { Standard = CarrierStandard.Sc, ScModulation = ScModulation.Qpsk, Data = "PN15" });
        Complex[] x = ScQam.Generate(q), xo = ScQam.Generate(q with { Offset = true });
        double qpsk = PaprDb(x), oqpsk = PaprDb(xo), dip = x.Min(v => v.Magnitude), dipO = xo.Min(v => v.Magnitude);
        output.WriteLine($"RRC 0.35, PN15: QPSK {qpsk:F2} dB (least |x| {dip:F3}), OQPSK {oqpsk:F2} dB (least |x| {dipO:F3})");
        // the peak drops ~0.4 dB; the gain is the envelope's floor: QPSK passes through zero, OQPSK stays above 0.4 rms
        Assert.True(oqpsk < qpsk - 0.3, $"QPSK {qpsk:F2} dB, OQPSK {oqpsk:F2} dB");
        Assert.True(dip < 0.01 && dipO > 0.4, $"least |x|: QPSK {dip:F3}, OQPSK {dipO:F3}");
        Assert.True(PaprDb(ScQam.Generate(Sc("QPSK") with { Pulse = ScPulse.HalfSine, Offset = true })) < 1e-9);
        Assert.True(PaprDb(ScQam.Generate(Sc("SOQPSK-TG"))) < 1e-9);
    }

    [Fact]
    public void V4_Edge_PaprIsNear3Point2Db()
    {
        var edge = ScQam.Close(ScQamPresets.All[0].Shape with { Guard = 0, Ramp = 0, FrameData = 0, Preamble = 0, DataSymbols = 511 });
        double papr = PaprDb(ScQam.Generate(edge));
        output.WriteLine($"EDGE 8PSK, continuous, PN9 ({edge.RecordSymbols} symbols): PAPR {papr:F2} dB");
        Assert.InRange(papr, 2.9, 3.5);
    }

    [Fact]
    public void V5_Bursts_AreWholeAndRamped()
    {
        var s = ScQamPresets.All[0].Shape;   // EDGE: 26 + 122 symbols, 8.25 guard, 4-symbol ramps
        const int sps = ScQam.SamplesPerSymbol;
        Assert.Equal(0, s.DataSymbols % 122);
        Assert.Equal(148, s.FrameSymbols);
        Assert.Equal(s.Frames * (148 * sps + 66), s.Samples);
        var w = ScQam.Window(s);
        var x = ScQam.Generate(s);
        int a0 = 148 * sps + 66 - sps / 2;                 // the second burst's first symbol period starts here
        Assert.Equal(0.5, w[a0 - 2 * sps], 12);            // half way up its 4-symbol ramp
        Assert.Equal(1, w[a0], 12);
        Assert.Equal(0, w[a0 - 4 * sps - 1], 12);          // before the ramp: the guard, silent
        Assert.Equal(0, x[a0 - 4 * sps - 1].Magnitude, 12);
        for (int i = a0 - 4 * sps; i < a0; i++)
            Assert.Equal(Math.Pow(Math.Sin(Math.PI / 2 * (i - a0 + 4 * sps) / (4.0 * sps)), 2), w[i], 12);
        Assert.Equal(s.Samples / (sps * s.SymbolRate), s.RecordSeconds, 15);
    }

    public static TheoryData<string> Variants()
    {
        var d = new TheoryData<string>();
        foreach (var p in ScQamPresets.All) d.Add(p.Name);
        foreach (var v in new[] { "RC", "Gaussian", "Rect", "OQPSK RRC", "π/4-QPSK", "RRC burst" }) d.Add(v);
        return d;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void V6_EveryVariant_ReadsACleanEvmBelowMinus50Db(string name)
    {
        var q = Sc("QPSK");
        var s = name switch
        {
            "RC" => q with { Pulse = ScPulse.Rc },
            "Gaussian" => q with { Pulse = ScPulse.Gaussian, Bt = 0.5 },
            "Rect" => q with { Pulse = ScPulse.Rect },
            "OQPSK RRC" => q with { Offset = true },
            "π/4-QPSK" => ScQam.Close(q with { RotationDeg = 45 }),
            "RRC burst" => ScQam.Close(Sc("16QAM") with { FrameData = 200, Preamble = 16, Guard = 6, Ramp = 2 }),
            _ => ScQamPresets.All.Single(p => p.Name == name).Shape,
        };
        ScQam.Check(s);
        double evm = EvmDb(s);
        output.WriteLine($"{name}: {evm:F1} dB");
        Assert.True(evm < -50, $"{name}: {evm:F1} dB");
    }

    [Fact]
    public void V7_Soqpsk_TheOqpskDecisionsAreTheBitsSent()
    {
        var s = Sc("SOQPSK-TG");
        var sy = ScQam.Symbols(s);
        var cons = ScQam.Constellation(s);
        Assert.Equal(0, Enumerable.Range(0, sy.Points.Length).Count(k => ScQam.Decide(cons, sy.Points[k]) != sy.Labels[k]));
        // the TG pulse's own intersymbol interference spreads the points about QPSK
        double e = Math.Sqrt(sy.Points.Select((p, k) => Math.Pow((p - cons[sy.Labels[k]]).Magnitude, 2)).Average());
        output.WriteLine($"SOQPSK-TG against plain QPSK points: {20 * Math.Log10(e):F1} dB");
    }
}
