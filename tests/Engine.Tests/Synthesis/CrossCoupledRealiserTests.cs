using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Synthesis.CoupledResonatorRealiserTests;

namespace CircuitRF.Engine.Tests.Synthesis;

/// <summary>
/// The cross-coupled square (<see cref="CrossCoupledLoopRealiser"/>): the realised MCRC, written as a netlist line from
/// its stored text and run through the engine, places its two zeros within 3 % of those asked for and its band's centre
/// within 2 % and width within 20 %; the folded matrix it reads holds only the square's couplings; each refusal has its
/// code. The matrix's response against its polynomials is <c>CouplingMatrixTests</c>' (the 4–2 mirror case).
/// </summary>
public class CrossCoupledRealiserTests(ITestOutputHelper output)
{
    private const double Fbw = 0.05, Wz = 2.0;

    private static FilterSpec Spec(double fbw = Fbw, double wz = Wz)
    {
        double a = Math.Sqrt(1.0 + fbw * fbw / 4.0), h = wz * fbw / 2.0, r = Math.Sqrt(1.0 + h * h);
        return new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 4, F0 * (a - fbw / 2.0), F0 * (a + fbw / 2.0),
            Ripple, TransmissionZerosHz: [F0 * (r - h), F0 * (r + h)]);
    }

    [Fact]
    public void TheMirrorZerosFoldedMatrix_IsTheSquare()
    {
        double rl = -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -Ripple / 10.0));
        double[,] m = CouplingMatrix.Synthesise(4, rl, [-Wz, Wz]);
        foreach (var (i, j) in new[] { (1, 1), (2, 2), (3, 3), (4, 4), (1, 3), (2, 4) })
            Assert.True(Math.Abs(m[i, j]) < 1e-9, $"M[{i},{j}] = {m[i, j]}");
        Assert.True(m[1, 2] * m[2, 3] * m[3, 4] * m[1, 4] < 0.0, "k14 against k23 must have the opposite sign");
    }

    [Fact]
    public void TheRealisedSquare_PlacesItsZerosAndPassesItsBand()
    {
        var realiser = FilterRealisers.All.OfType<CrossCoupledLoopRealiser>().Single();
        var spec = Spec();
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

        // Each zero the deepest dip of |S21| on its side of the band, refined by golden section.
        var f = Enumerable.Range(0, 801).Select(i => F0 * (0.8 + 0.4 * i / 800.0)).ToArray();
        var s = SParameterEngine.Run(nl, f)["S"];
        double[] db = [.. Enumerable.Range(0, f.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude))];
        double Zero(Func<double, bool> side)
        {
            int i = Enumerable.Range(1, f.Length - 2).Where(j => side(f[j]) && db[j] < db[j - 1] && db[j] < db[j + 1])
                .MinBy(j => db[j]);
            return CoupledResonatorDesign.Golden(x => -S21Db(nl, x), f[i - 1], f[i + 1]);
        }
        double z1 = Zero(x => x < f1), z2 = Zero(x => x > f2);
        double centre = (f1 + f2) / 2.0, bw = f2 - f1;
        var want = spec.TransmissionZerosHz!;
        output.WriteLine($"{line}\n{r.Notes[0]}\nband {f1 / 1e9:F4}-{f2 / 1e9:F4} GHz, centre {centre / spec.CentreHz - 1:P2} off, "
                         + $"bandwidth {bw / (spec.F2Hz - spec.F1Hz) - 1:P1} off; zeros {z1 / 1e9:F4} ({z1 / want[0] - 1:P2}) and "
                         + $"{z2 / 1e9:F4} GHz ({z2 / want[1] - 1:P2})");
        Assert.InRange(z1 / want[0], 0.97, 1.03);
        Assert.InRange(z2 / want[1], 0.97, 1.03);
        Assert.InRange(centre / spec.CentreHz, 0.98, 1.02);
        Assert.InRange(bw / (spec.F2Hz - spec.F1Hz), 0.8, 1.2);
    }

    // ══ Refusals ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("band")]
    [InlineData("response")]
    [InlineData("order")]
    [InlineData("bandwidth")]
    [InlineData("zeros")]           // none
    [InlineData("zeros-one")]
    [InlineData("zeros-one-side")]
    [InlineData("zeros-unmirrored")]
    [InlineData("zeros-in-band")]
    public void ASpecTheSquareCannotBuild_IsRefusedWithItsCode(string why)
    {
        var good = Spec();
        var z = good.TransmissionZerosHz!;
        var spec = why switch
        {
            "band" => good with { Band = FilterBand.Bandstop },
            "response" => good with { Response = FilterResponse.Elliptic },
            "order" => good with { Order = 5 },
            "bandwidth" => Spec(fbw: 0.3),
            "zeros" => good with { TransmissionZerosHz = null },
            "zeros-one" => good with { TransmissionZerosHz = [z[1]] },
            "zeros-one-side" => good with { TransmissionZerosHz = [z[1], 1.1 * z[1]] },
            "zeros-unmirrored" => good with { TransmissionZerosHz = [z[0], 1.02 * z[1]] },
            _ => Spec(wz: 0.9),
        };
        var realiser = new CrossCoupledLoopRealiser();
        Assert.False(realiser.Supports(spec, out var message));
        Assert.Contains("Cross-coupled synthesis", message);
        var ex = Assert.Throws<ParameterRefusalException>(() => realiser.Realise(spec, Board));
        Assert.Equal("synthesis.xc." + why.Split('-')[0], ex.Diagnostic!.Id);
    }

    [Fact]
    public void NoSubstrate_IsRefusedWithItsCode()
        => Assert.Equal("synthesis.xc.substrate",
            Assert.Throws<ParameterRefusalException>(() => new CrossCoupledLoopRealiser().Realise(Spec(), null)).Diagnostic!.Id);
}
