using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// A floating source feeding a diode bridge through a series bead network. With the diodes removed,
/// the source and the bead internals reach ground only through gmin (~1e11 Ω common mode), so a linear
/// back-solve that injects the solve's device currents amplifies the converged Newton residual by that
/// impedance: hundreds of kilovolts on every node behind a bead while the diode nodes read correctly.
/// The source node sits behind a bead whose DC resistance is 35 mΩ, so it must follow the diode node it
/// feeds — the oracle is that physical fact, not another circuitRF path.
/// </summary>
public class HbFloatingIslandBackSolveTests
{
    private const string Cnl = """
        define BEAD (p1 p2)
          R:R1  p1 1  R=163.4
          L:L1  p1 1  L=4.425e-7
          C:C1  p1 2  C=2.828e-13
          R:R2  1 2   R=61.04
          L:L2  1 2   L=9.984e-7
          R:R3  2 3   R=62.71
          L:L3  2 3   L=4.598e-8
          R:R4  3 p2  R=0.035
        end BEAD

        PnTone:P2  Vacp  Vacmin  Z=50 Ohm  Freq[1]=50 Hz  Pavl[1]=20 dBm  Freq[2]=1 kHz  Pavl[2]=15 dBm
        BEAD:X2  n1  Vacp
        BEAD:X3  n2  Vacmin
        Diode:D1  n1  out  Is=1e-14
        Diode:D3  0  n1  Is=1e-14
        Diode:D4  n2  out  Is=1e-14
        Diode:D2  0  n2  Is=1e-14
        C:C1  out  0  C=300 uF
        analysis HB1 type=hb NumFreqs=2 Tone[1]=50 ToneUnit[1]=Hz Tone[2]=1 ToneUnit[2]=kHz MaxHarm=7 MaxMixOrder=5 Tol=1e-6
        """;

    [Fact]
    public void NodeBehindABead_FollowsTheDiodeNodeItFeeds()
    {
        var (lib, tb) = new CnlReader().Read(Cnl, "tb");
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var run = new HbEngine(nl, tb).Run(p);
        Assert.True(run.Converged);

        DataCube v = run.DataSet["V"];
        string[] nodes = v.Axes[0].Labels!;
        Complex At(string node, int mix) => (Complex)v[Array.IndexOf(nodes, node), mix];

        // DC and the first few products: the bead is milliohms-to-ohms against volts, so millivolts.
        for (int mix = 0; mix < 4; mix++)
        {
            Assert.True((At("Vacp", mix) - At("n1", mix)).Magnitude < 5e-3,
                $"mix {mix}: Vacp {At("Vacp", mix)} vs n1 {At("n1", mix)}");
            Assert.True((At("Vacmin", mix) - At("n2", mix)).Magnitude < 5e-3,
                $"mix {mix}: Vacmin {At("Vacmin", mix)} vs n2 {At("n2", mix)}");
        }
    }
}
