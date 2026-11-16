using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MDLY, the delay line, through the engine: θ at F in every style, the part equal to its plan's sections wired by hand
/// (MLIN, MCURVE, MMITER, MBEND and, with Coupled, MCLINN), and what modelling a meander's coupling does to its phase.
/// </summary>
public class DelayLineSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        """;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Part(string spec) => $"{Ports}\nMDLY:D1 p1 p2 Z0=50 Ohm F=2.4 GHz {spec} {Sub}";

    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        return new Complex[,] { { (Complex)s[0, 0, 0], (Complex)s[0, 0, 1] }, { (Complex)s[0, 1, 0], (Complex)s[0, 1, 1] } };
    }

    /// <summary>arg S21 + θ in degrees, wrapped into (−180, 180].</summary>
    private static double PhaseError(Complex s21, double thetaDeg)
    {
        double d = s21.Phase * 180.0 / Math.PI + thetaDeg;
        d %= 360.0;
        if (d <= -180.0) d += 360.0;
        if (d > 180.0) d -= 360.0;
        return d;
    }

    [Theory]
    [InlineData("Theta=90 deg Style=Straight", 90.0)]
    [InlineData("Theta=270 deg Style=Corner Corner=Curve", 270.0)]
    [InlineData("Theta=270 deg Style=Corner Corner=Miter", 270.0)]
    [InlineData("Theta=270 deg Style=Corner Corner=Square", 270.0)]
    [InlineData("Theta=90 deg Style=Meander Corner=Curve Turns=1", 90.0)]
    [InlineData("Theta=360 deg Style=Meander Corner=Curve Turns=3", 360.0)]
    [InlineData("Theta=360 deg Style=Meander Corner=Miter Turns=3 Side=-1", 360.0)]
    [InlineData("Theta=270 deg Style=Meander Corner=Square Turns=2", 270.0)]
    public void S21_IsMinusThetaAtF(string spec, double thetaDeg)
    {
        double err = PhaseError(SAt(Part(spec), 2.4e9)[1, 0], thetaDeg);
        output.WriteLine($"{spec}: S21 phase error {err:F3}°");
        Assert.True(Math.Abs(err) < 0.5, $"S21 phase {err:F3}° off −θ at F");
    }

    /// <summary>The plan's own sections as explicit MLIN, MCURVE, MMITER, MBEND and MCLINN instances.</summary>
    private static string ByHand(PassivePlan plan)
    {
        string Node(int n) => n <= 2 ? $"p{n}" : $"i{n}";
        var b = new StringBuilder(Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", (section is PlanCoupledStrips m ? m.ModelNodes : section.Nodes).Select(Node));
            b.AppendLine(section switch
            {
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanBend { M: { } mi } bend => $"MMITER:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg M={F(mi)} {Sub}",
                PlanBend bend => $"MBEND:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg Miter=0 {Sub}",
                PlanCoupledStrips strips => $"MCLINN:K{++k} {nodes} N={strips.W.Count} W={F(strips.W[0])} S={F(strips.S[0])} "
                                          + $"L={F(strips.L)} {Sub}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    [Theory]
    [InlineData("Theta=360 deg Style=Meander Corner=Curve Turns=3", PlanRouteStyle.Meander, PlanCorner.Curve, 3, false)]
    [InlineData("Theta=360 deg Style=Meander Corner=Miter Turns=2 Coupled=1", PlanRouteStyle.Meander, PlanCorner.Miter, 2, true)]
    [InlineData("Theta=270 deg Style=Corner Corner=Square", PlanRouteStyle.Corner, PlanCorner.Square, 3, false)]
    public void TheLine_IsItsSectionsWiredByHand(string spec, PlanRouteStyle style, PlanCorner corner, int turns, bool coupled)
    {
        double theta = (spec.Contains("Theta=360") ? 360.0 : 270.0) * Math.PI / 180.0;
        var plan = DelayLinePlan.Build(50.0, 2.4e9, theta, Ro4350b, style, corner, turns, coupled: coupled).Plan;
        Assert.Equal(coupled, plan.Sections.OfType<PlanCoupledStrips>().Any());
        foreach (double f in new[] { 1.2e9, 2.4e9, 4.8e9 })
        {
            var (a, b) = (SAt(Part(spec), f), SAt(ByHand(plan), f));
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
        }
    }

    /// <summary>Coupled = 1 puts the meander's legs in one N-line section. Its phase at F moves away from −θ as the legs
    /// close: little with them 9W apart edge to edge (what is left is the section's quasi-static εeff against MLIN's
    /// dispersive one), degrees at 3W, where the uncoupled meander's warning starts, and more at W. Separate legs hold
    /// −θ at every pitch.</summary>
    [Fact]
    public void ModellingTheLegsCoupling_MovesThePhaseAsTheyClose()
    {
        double previous = 0.0;
        foreach (double pitch in new[] { 11e-3, 4.4e-3, 2.2e-3 })         // W = 1.1 mm: 10W, 4W and 2W
        {
            string spec = $"Theta=360 deg Style=Meander Corner=Curve Turns=3 Pitch={F(pitch)}";
            double apart = PhaseError(SAt(Part(spec), 2.4e9)[1, 0], 360.0);
            double coupled = PhaseError(SAt(Part(spec + " Coupled=1"), 2.4e9)[1, 0], 360.0);
            output.WriteLine($"pitch {pitch * 1e3} mm: S21 phase error {apart:F3}° as separate legs, {coupled:F3}° coupled");
            Assert.True(Math.Abs(apart) < 0.5, $"separate legs {apart:F3}°");
            Assert.True(Math.Abs(coupled) > Math.Abs(previous), $"coupled legs {coupled:F3}° at {pitch} after {previous:F3}°");
            Assert.True(Math.Abs(coupled) < 10.0, $"coupled legs {coupled:F3}°");
            previous = coupled;
        }
    }
}
