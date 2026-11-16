using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Synthesis;

/// <summary>
/// MCRC's realisers: each form's realised part, written as a netlist line from the parameters exactly as the
/// realisation stores them and run through the engine, passes its design's band (centre within 2 %, ripple bandwidth
/// within 15 %, the closed forms' and the circuit model's own accuracy); and each refusal has its code.
/// </summary>
public class CoupledResonatorRealiserTests(ITestOutputHelper output)
{
    // RO4350B, 20 mil, lossless, so the passband ripple is the design's.
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e20 TanD=0";
    private static readonly MicrostripSubstrate Board = new(0.508e-3, 35e-6, 3.66, 1e20, 0.0);
    private const double F0 = 2.4e9, Ripple = 0.5;

    private static FilterSpec Band(double fbw, int order = 3, double z0 = 50.0)
    {
        double a = Math.Sqrt(1.0 + fbw * fbw / 4.0);
        return new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, order, F0 * (a - fbw / 2.0), F0 * (a + fbw / 2.0),
            Ripple, Z0: z0);
    }

    [Theory]
    [InlineData(typeof(EdgeCoupledRealiser), 0.10)]
    [InlineData(typeof(ZigZagRealiser), 0.10)]
    [InlineData(typeof(HairpinRealiser), 0.10)]
    [InlineData(typeof(TappedHairpinRealiser), 0.10)]
    [InlineData(typeof(SirHairpinRealiser), 0.05)]
    [InlineData(typeof(OpenLoopRealiser), 0.05)]
    public void TheRealisedPart_PassesItsBand(Type form, double fbw)
    {
        var realiser = FilterRealisers.All.Single(r => r.GetType() == form);
        var spec = Band(fbw);
        Assert.True(realiser.Supports(spec, out _));
        var r = realiser.Realise(spec, Board);
        Assert.Equal("MCRC", r.PartType);
        string line = string.Join(" ", r.Parameters.Select(p => $"{p.Key}={p.Value}"));
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            MCRC:X1 p1 p2 {line} {Sub}
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var (f1, f2) = RippleEdges(nl, 0.85 * F0, 1.15 * F0);
        double centre = (f1 + f2) / 2.0, bw = f2 - f1;
        output.WriteLine($"{realiser.Name}: {line}; band {f1 / 1e9:F4}-{f2 / 1e9:F4} GHz, centre {centre / spec.CentreHz - 1:P2} "
                         + $"off, bandwidth {bw / (spec.F2Hz - spec.F1Hz) - 1:P1} off");
        Assert.InRange(centre / spec.CentreHz, 0.98, 1.02);
        Assert.InRange(bw / (spec.F2Hz - spec.F1Hz), 0.85, 1.15);
    }

    private static double S21Db(ElaboratedNetlist nl, double f)
        => 20.0 * Math.Log10(((Complex)SParameterEngine.Run(nl, [f])["S"][0, 1, 0]).Magnitude);

    /// <summary>The lowest and highest frequencies in [lo, hi] where |S21| is within the ripple of 0 dB, each refined by
    /// bisection.</summary>
    private static (double F1, double F2) RippleEdges(ElaboratedNetlist nl, double lo, double hi)
    {
        var f = Enumerable.Range(0, 601).Select(i => lo + (hi - lo) * i / 600.0).ToArray();
        var s = SParameterEngine.Run(nl, f)["S"];
        bool[] inside = [.. Enumerable.Range(0, f.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude) >= -Ripple)];
        int i1 = Array.IndexOf(inside, true), i2 = Array.LastIndexOf(inside, true);
        Assert.True(i1 > 0 && i2 < f.Length - 1, $"no passband inside {lo / 1e9}-{hi / 1e9} GHz");
        double Edge(double a, double b)   // a outside, b inside
        {
            for (int k = 0; k < 40; k++) { double m = 0.5 * (a + b); if (S21Db(nl, m) >= -Ripple) b = m; else a = m; }
            return 0.5 * (a + b);
        }
        return (Edge(f[i1 - 1], f[i1]), Edge(f[i2 + 1], f[i2]));
    }

    // ══ Refusals ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("band")]
    [InlineData("zeros")]
    [InlineData("response")]
    [InlineData("order")]
    [InlineData("bandwidth")]
    public void ASpecNoFormBuilds_IsRefusedWithItsCode(string code)
    {
        var good = Band(0.1);
        var spec = code switch
        {
            "band" => good with { Band = FilterBand.Lowpass },
            "zeros" => good with { TransmissionZerosHz = [2.0e9] },
            "response" => good with { Response = FilterResponse.Elliptic },
            "order" => good with { Order = CoupledResonatorSpec.MaxN + 1 },
            _ => Band(0.3),
        };
        foreach (var realiser in FilterRealisers.All.OfType<CoupledResonatorRealiser>())
        {
            Assert.False(realiser.Supports(spec, out var why));
            Assert.Contains("Coupled-resonator synthesis", why);
            var ex = Assert.Throws<ParameterRefusalException>(() => realiser.Realise(spec, Board));
            Assert.Equal("synthesis.mcrc." + code, ex.Diagnostic!.Id);
        }
    }

    [Fact]
    public void WhatTheSubstrateCannotGive_IsRefusedWithItsCode()
    {
        string Code(Action a) => Assert.Throws<ParameterRefusalException>(a).Diagnostic!.Id;
        var edge = new EdgeCoupledRealiser();
        Assert.Equal("synthesis.mcrc.substrate", Code(() => edge.Realise(Band(0.1), null)));
        Assert.Equal("synthesis.mcrc.width-unreachable", Code(() => edge.Realise(Band(0.1, z0: 2000.0), Board)));
        Assert.Equal("synthesis.mcrc.out-of-reach", Code(() => CoupledResonatorDesign.GapFor(1e-3, 0.9, F0, Board)));
        var crossed = new double[3, 3];
        crossed[0, 2] = 0.01;
        Assert.Equal("synthesis.mcrc.cross-coupling", Code(() => CoupledResonatorDesign.FromCouplings(
            new Dictionary<string, object> { ["W"] = 1e-3, ["Shape"] = "Hairpin", ["Sa"] = 2e-3 }, crossed, 20.0, F0, Board)));
    }
}
