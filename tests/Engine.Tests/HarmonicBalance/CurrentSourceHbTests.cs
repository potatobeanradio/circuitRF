using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Both current sources in HARMONIC BALANCE — the question "does this work outside the linear
/// analyses?", answered by measurement rather than by the fact that both models declare
/// <c>ModelKind.Linear</c>.
///
/// <para>Both are linear devices, so HB stamps them into its linear partition at every retained
/// harmonic exactly as it stamps a resistor. Each test hangs its device on a LINEAR-ONLY sub-network
/// whose answer is a one-line closed form, alongside (but not touching) a nonlinear device that
/// makes the run a real HB solve rather than a linear one in disguise.</para>
/// </summary>
public class CurrentSourceHbTests(ITestOutputHelper output)
{
    // A saturating SDD on its own Vdc-fed branch. It shares no node with the sub-network under
    // test, so it supplies the nonlinear partition without perturbing the closed form.
    private const string NonlinearBallast = @"
Vdc:Vb    n_b 0    Vdc=1 V
R:Rb      n_b n_d  R=1000 Ohm
SDD:D1    n_d 0    Ports=1  I[1,0]=1e-3*tanh(10.0*_v1)
";

    private static DataSet RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        return (DataSet)new HbEngine(netlist, tb).Run(p);
    }

    private static Complex V(DataCube cube, string node, int k)
    {
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        Assert.True(i >= 0, $"node '{node}' missing from the V cube's node axis");
        return (Complex)cube[i, k];
    }

    // ── ITone drives a resistor in HB: V[k=1] = I·R, and nothing at k=2 ───────
    //
    //   I_1Tone:Is  n1 0  Freq=2e9  I=1e-3  →  1 mA into 1 kOhm  →  V(n1, k=1) = 1 V.
    //   The DC offset is 0, so V(n1, k=0) = 0; the source has no second-harmonic content and the
    //   branch is linear, so V(n1, k=2) = 0 as well. All three together are what says the source is
    //   excited at ITS OWN tone and is an open everywhere else, which is the whole model.
    [Fact]
    public void ITone_InHb_DrivesItsOwnHarmonicOnly()
    {
        var ds = RunHb(@"
I_1Tone:Is  n1 0   Freq=2e9  I=1e-3  Phase=0  Idc=0
R:R1        n1 0   R=1000 Ohm
" + NonlinearBallast + @"
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-8
");
        var v = ds["V"];
        Complex k0 = V(v, "n1", 0), k1 = V(v, "n1", 1), k2 = V(v, "n1", 2);
        output.WriteLine($"V(n1): k=0 {k0:G4}   k=1 {k1:G4}   k=2 {k2:G4}");

        Assert.True((k1 - new Complex(1.0, 0)).Magnitude < 1e-6,
            $"V(n1) at the fundamental should be I·R = 1 V, got {k1}");
        Assert.True(k0.Magnitude < 1e-6, $"no DC offset was stated, so V(n1,k=0) should be 0, got {k0}");
        Assert.True(k2.Magnitude < 1e-6, $"a linear branch cannot make a 2nd harmonic, got {k2}");
    }

    // The DC offset reaches k=0 and only k=0 — the other half of ExcitationAt's frequency switch.
    [Fact]
    public void ITone_InHb_IdcAppearsAtDcOnly()
    {
        var ds = RunHb(@"
I_1Tone:Is  n1 0   Freq=2e9  I=1e-3  Phase=0  Idc=2e-3
R:R1        n1 0   R=1000 Ohm
" + NonlinearBallast + @"
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-8
");
        var v = ds["V"];
        Assert.True((V(v, "n1", 0) - new Complex(2.0, 0)).Magnitude < 1e-6);   // Idc·R
        Assert.True((V(v, "n1", 1) - new Complex(1.0, 0)).Magnitude < 1e-6);   // I·R
    }

    // ── VCCS in HB: the transconductance holds at every harmonic ─────────────
    //
    //   V_1Tone:Vs (Vdc=0.5, 0.1 V at 2 GHz) sets V(nin); the VCCS senses nin and drives nout
    //   through RL = 100 Ohm. Current flows DOWN through the source — out of nout — so at EVERY
    //   harmonic k the stage is inverting:
    //       V(nout, k) = −G · V(nin, k) · RL = −0.01 · V(nin,k) · 100 = −1.0 · V(nin, k).
    //   Asserting it at k=0 AND k=1 is the point: a device stamped only into the DC solve, or only
    //   at the fundamental, passes one of the two and fails the other.
    [Fact]
    public void Vccs_InHb_TransconductanceHoldsAtDcAndAtTheFundamental()
    {
        var ds = RunHb(@"
V_1Tone:Vs  nin 0        Vdc=0.5  Freq=2e9  V=0.1  Phase=0
R:Rin       nin 0        R=1000 Ohm
VCCS:G1     nout 0 nin 0 G=0.01
R:RL        nout 0       R=100 Ohm
" + NonlinearBallast + @"
analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-8
");
        var v = ds["V"];
        for (int k = 0; k <= 2; k++)
        {
            Complex vin = V(v, "nin", k), vout = V(v, "nout", k);
            output.WriteLine($"k={k}: V(nin)={vin:G6}  V(nout)={vout:G6}");
            Assert.True((vout + vin).Magnitude < 1e-6,
                $"k={k}: V(nout) should be −G·RL·V(nin) = −1.0·V(nin) = {-vin}, got {vout}");
        }

        // And the drive really is present at both harmonics, so the equality above is not the
        // trivially true 0 == 0.
        Assert.True(V(v, "nin", 0).Magnitude > 0.4, "the DC bias should reach nin");
        Assert.True(V(v, "nin", 1).Magnitude > 0.05, "the fundamental should reach nin");
    }

    // ── An ITone-driven node is a genuine HB source into a NONLINEAR device ───
    //
    //   The current source drives the SDD's own port through a shunt resistor, so this exercises the
    //   path the previous tests deliberately avoid: the source's injection reaching the extractor's
    //   open-circuit interface voltage. The claim is only that it converges and that the node sits
    //   where an ideal current source into that network puts it — below I·R, because the SDD's
    //   conductance is in parallel with R.
    [Fact]
    public void ITone_DrivingANonlinearInterfaceNode_ConvergesAndLoadsCorrectly()
    {
        var ds = RunHb(@"
I_1Tone:Is  n1 0   Freq=2e9  I=1e-3  Phase=0  Idc=0
R:R1        n1 0   R=1000 Ohm
SDD:D1      n1 0   Ports=1  I[1,0]=1e-3*tanh(10.0*_v1)

analysis HB1  type=hb  Tone=2e9  MaxHarm=5  Tol=1e-8
");
        var v = ds["V"];
        Complex k1 = V(v, "n1", 1);
        output.WriteLine($"V(n1,k=1) = {k1:G6}  (|.| = {k1.Magnitude:G6})");

        // The SDD's small-signal conductance at V=0 is 1e-2 S, ten times R1's 1e-3 S, so the node
        // must sit WELL below the 1 V an unloaded 1 kOhm would give, and above zero.
        Assert.InRange(k1.Magnitude, 1e-3, 0.5);
    }
}
