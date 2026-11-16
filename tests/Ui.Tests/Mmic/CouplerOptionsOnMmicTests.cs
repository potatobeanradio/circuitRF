using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Mmic;

/// <summary>
/// The coupler options and the Schiffman phase shifter on the shipped GaAs process, as a run gets them: the substrate
/// injected from the technology as for any microstrip part. Each builds at 10 GHz and does what it is for there. The
/// process's broadside pair resolves (Metal2 over the MIM plate), but 2.55 µm of air under one weighted εr is no
/// coupler, so Broadside is held on the board tests.
/// </summary>
public class CouplerOptionsOnMmicTests(ITestOutputHelper output)
{
    private const double F0 = 10e9;

    private static Complex[,] SAt(string cnl, int n)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [F0])["S"];
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);
    private static double Wrap(double deg) => deg - 360.0 * Math.Round(deg / 360.0);

    [Fact]
    public void TheOptionsAndThePhaseShifter_BuildOnTheGaAsProcess()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        string sub = string.Join(" ", MicrostripSubstrateInjection.BuildOverrides(tech, out _, null, null)
            .Select(o => $"{o.Name}={o.Expression}"));
        var (stack, reason) = SubstrateResolver.ResolveBroadsideStack(tech, null);
        Assert.True(stack is not null, reason);
        string ports = string.Join("\n", Enumerable.Range(1, 4).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm"));
        string coupler = "MCLC:C1 p1 p2 p3 p4 Z0=50 Ohm F=10 GHz";

        var sections = SAt($"{ports}\n{coupler} Coupling=15 dB Sections=3 Band=2 {sub}", 4);
        var tandem = SAt($"{ports}\n{coupler.Replace("p4", "p4 j1 j2 j3 j4")} Coupling=3 dB Tandem=1 {sub}\n"
                       + "Short:X1 j1 j2\nShort:X2 j3 j4", 4);
        var wiggly = SAt($"{ports}\n{coupler} Coupling=10 dB Compensation=Wiggly {sub}", 4);
        var schiffman = SAt($"{ports}\nMSPS:S1 p1 p2 p3 p4 Z0=50 Ohm F=10 GHz Phase=90 deg Band=1.5 {sub}", 4);
        output.WriteLine($"stack {(stack!.Stripline ? "BCLIN" : "MBCLIN")} {stack.LayerA}/{stack.LayerB}: Hs {stack.Hs * 1e6:F2} um, "
                       + $"Hb {stack.Hb * 1e6:F1} um, er {stack.EpsR:F2}");
        output.WriteLine($"sections S31 {Db(sections[2, 0]):F2} dB; tandem S21 {Db(tandem[1, 0]):F2} S31 {Db(tandem[2, 0]):F2} dB; "
                       + $"wiggly S31 {Db(wiggly[2, 0]):F2} S41 {Db(wiggly[3, 0]):F2} dB; "
                       + $"Schiffman {Wrap((schiffman[1, 0].Phase - schiffman[3, 2].Phase) * 180 / Math.PI):F2} deg");

        Assert.InRange(Db(sections[2, 0]), -16.0, -14.0);
        Assert.InRange(Db(tandem[2, 0]), -4.0, -2.5);
        Assert.InRange(Db(tandem[1, 0]), -4.0, -2.5);
        Assert.True(Db(wiggly[2, 0]) - Db(wiggly[3, 0]) > 20.0, "wiggly directivity");
        Assert.Equal(("Metal2", "MIM Metal"), (stack.LayerA, stack.LayerB));
        Assert.InRange(Wrap((schiffman[1, 0].Phase - schiffman[3, 2].Phase) * 180 / Math.PI), 89.0, 91.0);
    }
}
