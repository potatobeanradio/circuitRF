using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The ladder's radial ends and lumped sites through the engine. A radial-stub and a butterfly lowpass are their MLIN,
/// MTEE, MCROSS and MRADIAL wired by hand; and a lumped LC lowpass written as sites alone, filled with ideal parts of the
/// values its sites state, cuts off where its g-values put it.
/// </summary>
public class LadderSitesSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        """;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

    /// <summary>The ladder as one instance, its G pins on nets g1…, then <paramref name="tail"/>.</summary>
    private static string Ladder(string sections, string tail = "")
    {
        int g = LadderPlan.Grounds(sections);
        string nets = string.Concat(Enumerable.Range(1, g).Select(k => $" g{k}"));
        return $"{Ports}\nMLAD:X1 p1 p2{nets} Sections=\"{sections}\" Z0=50 Ohm F=2.4e9 {Sub}\n{tail}";
    }

    /// <summary>The plan's own sections as ordinary instances: a G pin on its net, a radial end an MRADIAL.</summary>
    private static string ByHand(PassivePlan plan, string tail = "")
    {
        string Node(int n) => n <= 2 ? $"p{n}" : n <= plan.Pins.Count ? $"g{n - 2}" : $"i{n}";
        var b = new StringBuilder(Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanCross x => $"MCROSS:X{++k} {nodes} W1={F(x.W1)} W2={F(x.W2)} W3={F(x.W3)} W4={F(x.W4)} "
                             + $"L1={F(x.L1)} L2={F(x.L2)} L3={F(x.L3)} L4={F(x.L4)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanRadialStub r => $"MRADIAL:R{++k} {nodes} W={F(r.W)} Ro={F(r.Ro)} Angle={F(r.AngleDeg)} deg Sides={r.Sides} {Sub}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.Append(tail).ToString();
    }

    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static void AssertSame(Complex[,] a, Complex[,] b, double f)
    {
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>A radial-stub lowpass: 100 Ω lines between radial stubs on short feeds, one sector up, a cross of two, one
    /// down. The feeds' far ends, where each sector's narrow end sits, are its shunt nodes.</summary>
    private const string RadialLowpass =
        "line(W=1.1mm, L=3mm); stub(W=0.6mm, L=0.5mm, end=radial, Ro=6mm, angle=60); line(W=0.22mm, L=6mm); "
      + "stub(W=0.6mm, L=0.5mm, end=radial, Ro=6mm, angle=60, side=both); line(W=0.22mm, L=6mm); "
      + "stub(W=0.6mm, L=0.5mm, end=radial, Ro=6mm, angle=60, side=down); line(W=1.1mm, L=3mm)";

    /// <summary>A butterfly lowpass: butterflies at the ends of short feeds off the 100 Ω line, one either side.</summary>
    private const string ButterflyLowpass =
        "line(W=1.1mm, L=3mm); stub(W=0.6mm, L=3mm, end=butterfly, Ro=6mm, angle=60); line(W=0.22mm, L=8mm); "
      + "stub(W=0.6mm, L=3mm, end=butterfly, Ro=6mm, angle=60, side=down); line(W=1.1mm, L=3mm)";

    [Theory]
    [InlineData(RadialLowpass, 1e9)]
    [InlineData(RadialLowpass, 4e9)]
    [InlineData(ButterflyLowpass, 1e9)]
    [InlineData(ButterflyLowpass, 4e9)]
    public void ARadialEnd_IsMradialOnItsFeed_WiredByHand(string sections, double f)
    {
        var plan = LadderPlan.Build(sections, 50.0, 2.4e9, Ro4350b).Plan;
        Assert.NotEmpty(plan.Sections.OfType<PlanRadialStub>());
        AssertSame(SAt(Ladder(sections), f), SAt(ByHand(plan), f), f);
    }

    /// <summary>Both are lowpass filters: they pass at 0.5 GHz and stop near where their sectors resonate.</summary>
    [Theory]
    [InlineData(RadialLowpass)]
    [InlineData(ButterflyLowpass)]
    public void TheRadialAndButterflyLowpasses_PassLowAndStopHigh(string sections)
    {
        var cnl = Ladder(sections);
        double[] f = [0.5e9, 1e9, 2e9, 3e9, 3.5e9, 4e9, 4.5e9, 5e9];
        var s21 = f.Select(x => Db(SAt(cnl, x)[1, 0])).ToArray();
        output.WriteLine(string.Join(", ", f.Zip(s21, (x, y) => $"{x / 1e9} GHz {y:F1} dB")));
        Assert.True(s21[0] > -1.0, $"{s21[0]:F2} dB at 0.5 GHz");
        Assert.True(s21.Min() < -20.0, $"{s21.Min():F1} dB at best");
    }

    /// <summary>
    /// The third-order lumped Butterworth lowpass, series L, shunt C, series L, at fc = 200 MHz: L = g·R0/ωc and
    /// C = g/(R0·ωc) with g = 1, 2, 1 (Pozar §8.3, textbook, no DOI), written as three sites. Its copper is two end pads, a
    /// tee and the shunt site's pad. Filled with ideal parts of the values its sites state, as Place Site Parts would fill
    /// them, its −3 dB point is fc to ±2 %. The pads are in the model and not in the closed form: the shunt pad's own
    /// inductance in series with C lowers the cutoff about 5 % at 1 GHz on this board, and about 1 % here.
    /// </summary>
    [Fact]
    public void ALumpedLcLowpassOfSites_FilledWithItsValues_CutsOffAtFc()
    {
        const double fc = 200e6, r0 = 50.0, wc = 2.0 * Math.PI * fc;
        string sections = LadderPlan.Format(
        [
            new LadderSite(PlanSiteRole.L, 1.0 * r0 / wc),
            new LadderSite(PlanSiteRole.C, 2.0 / (r0 * wc), PlanSiteKind.Shunt),
            new LadderSite(PlanSiteRole.L, 1.0 * r0 / wc),
        ]);
        var plan = LadderPlan.Build(sections, 50.0, 2.4e9, Ro4350b).Plan;
        var parts = new StringBuilder();
        int k = 0;
        foreach (var site in plan.Sites)
        {
            string a = $"g{site.PinA[1..]}", b = site.PinB is { } pb ? $"g{pb[1..]}" : "0";
            parts.AppendLine(site.Role == PlanSiteRole.L
                ? $"L:L{++k} {a} {b} L={F(site.Value!.Value)}"
                : $"C:C{++k} {a} {b} C={F(site.Value!.Value)}");
        }
        string cnl = Ladder(sections, parts.ToString());

        double S21(double f) => Db(SAt(cnl, f)[1, 0]);
        double lo = 0.5 * fc, hi = 1.5 * fc;
        Assert.True(S21(lo) > -3.0 && S21(hi) < -3.0);
        while (hi - lo > 1e3)
        {
            double mid = 0.5 * (lo + hi);
            if (S21(mid) > -3.0) lo = mid; else hi = mid;
        }
        double f3 = 0.5 * (lo + hi);
        output.WriteLine($"{sections}\n{parts}-3 dB at {f3 / 1e9:F4} GHz ({(f3 / fc - 1) * 100:F2} %)");
        Assert.InRange(f3 / fc, 0.98, 1.02);
    }
}
