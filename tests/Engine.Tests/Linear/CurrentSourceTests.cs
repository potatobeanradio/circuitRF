using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for the two current sources — the ideal tone current source
/// (<c>I_1Tone</c>/<c>I_nTone</c>) and the ideal voltage-controlled current source (<c>VCCS</c>).
///
/// <para>Every assertion here is a CLOSED-FORM answer from Ohm's law, not a comparison against
/// another circuitRF path, because the whole risk in a current source is a sign or a direction —
/// a flipped stamp still solves, still converges, and still looks plausible.</para>
/// </summary>
public class CurrentSourceTests(ITestOutputHelper output)
{
    /// <summary>
    /// Decimal places every DC assertion below uses. Not 9: the DC engine adds its own gmin
    /// (1e-9 S) from every node to ground, so a 1 kOhm divider lands ~1e-9 off the exact answer.
    /// Six places is still orders tighter than any sign flip or unit slip could hide in.
    /// </summary>
    private const int Tol = 6;

    private static (ElaboratedNetlist Nl, NonlinearDcEngine.DcResult Dc) RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (nl, NonlinearDcEngine.Run(nl));
    }

    private static double V(ElaboratedNetlist nl, NonlinearDcEngine.DcResult dc, string net)
        => dc.NodeVoltages[nl.Nodes.IndexOf(net) - 1];

    // ── ITone direction: current is DELIVERED to the first node ───────────────
    //
    //   I_1Tone:I1  n1 0  Idc=2e-3 (2 mA)   with R=1 kΩ from n1 to ground.
    //   The engine's convention (src/Engine/CLAUDE.md) is that J injects into the FIRST node, so
    //   2 mA flows out of n1 through R to ground and V(n1) = +2 V. A sign flip gives −2 V, which is
    //   just as convergent and completely wrong — that is the whole point of this test.
    [Fact]
    public void ITone_Dc_InjectsIntoItsFirstNode_PositiveVoltage()
    {
        var (nl, dc) = RunDc(@"
I_1Tone:I1  n1 0   Idc=2e-3  Freq=1e9  I=0
R:R1        n1 0   R=1000 Ohm

analysis DC1  type=dc
");
        Assert.True(dc.Converged, $"DC did not converge. Residual={dc.FinalResidual:G}");
        double v1 = V(nl, dc, "n1");
        output.WriteLine($"V(n1) = {v1:G6} V");
        Assert.Equal(+2.0, v1, Tol);
    }

    // Reversing the nets reverses the sign — the same statement from the other side, and the check
    // that the direction comes from the NET ORDER rather than from anything about ground.
    [Fact]
    public void ITone_Dc_ReversedNets_ReverseTheSign()
    {
        var (nl, dc) = RunDc(@"
I_1Tone:I1  0 n1   Idc=2e-3  Freq=1e9  I=0
R:R1        n1 0   R=1000 Ohm

analysis DC1  type=dc
");
        Assert.True(dc.Converged);
        Assert.Equal(-2.0, V(nl, dc, "n1"), Tol);
    }

    // ── ITone is an OPEN, not a short, off its tones ──────────────────────────
    //
    //   Vdc:V1 → R1 → n2 → R2 → gnd is a 5 V divider reading 10/3 V at n2. Hanging an ITone with
    //   NO dc component across R2 must not change that number by anything: an ideal current source
    //   contributes no admittance at all. (A voltage source in the same place would short it.)
    [Fact]
    public void ITone_WithNoDcComponent_IsAnOpenCircuit()
    {
        var (nl, dc) = RunDc(@"
Vdc:V1      n1 0   Vdc=5 V
R:R1        n1 n2  R=100 Ohm
R:R2        n2 0   R=200 Ohm
I_1Tone:I1  n2 0   Idc=0  Freq=1e9  I=0.001

analysis DC1  type=dc
");
        Assert.True(dc.Converged);
        Assert.Equal(10.0 / 3.0, V(nl, dc, "n2"), Tol);
    }

    // ── The multi-tone spelling shares the model ──────────────────────────────
    //
    //   I_nTone with Idc=2e-3 (2 mA) behaves at DC exactly as the single-tone spelling does, which is the
    //   claim that the two names are one model rather than two implementations.
    [Fact]
    public void InTone_SharesTheModel_SameDcAnswer()
    {
        var (nl, dc) = RunDc(@"
I_nTone:In  n1 0   Idc=2e-3  NumFreqs=2  Freq[1]=1e9 I[1]=1e-3 Phase[1]=0  Freq[2]=2e9 I[2]=5e-4 Phase[2]=0
R:R1        n1 0   R=1000 Ohm

analysis DC1  type=dc
");
        Assert.True(dc.Converged);
        Assert.Equal(+2.0, V(nl, dc, "n1"), Tol);
    }

    // ── A zero-Hz tone is reported, and names Idc as the right place for it ───
    [Fact]
    public void ITone_ZeroHzTone_WarnsAndNamesIdc()
    {
        var (lib, tb) = new CnlReader().Read(@"
I_1Tone:I1  n1 0   Freq=0  I=1e-3
R:R1        n1 0   R=1000 Ohm

analysis DC1  type=dc
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var warn = nl.Warnings.FirstOrDefault(w => w.Contains("Freq=0", StringComparison.Ordinal));
        Assert.NotNull(warn);
        Assert.Contains("Idc", warn);   // not "Vdc" — the message must name this source's own key
        output.WriteLine(warn!);
    }

    // ── VCCS: the textbook transconductance answer ────────────────────────────
    //
    //   Vdc:V1 sets V(nc) = 2 V through a divider-free direct connection.
    //   VCCS:G1 out+ = n_out, out− = 0, ctrl+ = nc, ctrl− = 0, G = 0.01 S (10 mS).
    //   I = 10 mS × 2 V = 20 mA drawn OUT of n_out (down through the source), so the 100 Ω load
    //   pulls it NEGATIVE: V(n_out) = −20 mA × 100 Ω = −2 V. The sign is the assertion — a VCCS is
    //   inverting across a grounded load, and the magnitude alone passes under either direction.
    [Fact]
    public void Vccs_Dc_SinksGTimesControlVoltageFromItsOutputPlusNode()
    {
        var (nl, dc) = RunDc(@"
Vdc:V1    nc 0        Vdc=2 V
R:Rc      nc 0        R=1000 Ohm
VCCS:G1   n_out 0 nc 0   G=0.01
R:RL      n_out 0     R=100 Ohm

analysis DC1  type=dc
");
        Assert.True(dc.Converged, $"DC did not converge. Residual={dc.FinalResidual:G}");
        double vout = V(nl, dc, "n_out");
        output.WriteLine($"V(n_out) = {vout:G6} V  (expect −10 mS · 2 V · 100 Ω = −2 V)");
        Assert.Equal(-2.0, vout, Tol);
    }

    // Swapping the CONTROL pair flips the sign; swapping the OUTPUT pair flips it too. Both are
    // stated because they are two different halves of the stamp and a symmetric bug fixes neither.
    [Fact]
    public void Vccs_Dc_SwappingEitherPair_FlipsTheSign()
    {
        var (nlC, dcC) = RunDc(@"
Vdc:V1    nc 0        Vdc=2 V
R:Rc      nc 0        R=1000 Ohm
VCCS:G1   n_out 0 0 nc   G=0.01
R:RL      n_out 0     R=100 Ohm

analysis DC1  type=dc
");
        Assert.Equal(+2.0, V(nlC, dcC, "n_out"), Tol);

        var (nlO, dcO) = RunDc(@"
Vdc:V1    nc 0        Vdc=2 V
R:Rc      nc 0        R=1000 Ohm
VCCS:G1   0 n_out nc 0   G=0.01
R:RL      n_out 0     R=100 Ohm

analysis DC1  type=dc
");
        Assert.Equal(+2.0, V(nlO, dcO, "n_out"), Tol);
    }

    // ── The control pair draws NO current — this is what "ideal" means ────────
    //
    //   The control node hangs off a 1 kΩ divider from a 4 V supply through 1 kΩ, so V(nc) = 2 V
    //   ONLY if the VCCS's control pair takes nothing. Any control-row entry in the stamp loads the
    //   divider and moves V(nc) — and, being a small shift, would otherwise pass a loose tolerance
    //   on the output voltage alone.
    [Fact]
    public void Vccs_ControlPairDrawsNoCurrent_DividerIsUnloaded()
    {
        var (nl, dc) = RunDc(@"
Vdc:V1    n1 0        Vdc=4 V
R:Ra      n1 nc       R=1000 Ohm
R:Rb      nc 0        R=1000 Ohm
VCCS:G1   n_out 0 nc 0   G=0.01
R:RL      n_out 0     R=100 Ohm

analysis DC1  type=dc
");
        Assert.True(dc.Converged);
        Assert.Equal(+2.0, V(nl, dc, "nc"), Tol);     // unloaded divider
        Assert.Equal(-2.0, V(nl, dc, "n_out"), Tol);  // −10 mS · 2 V · 100 Ω
    }

    // ── The VCCS is an OPEN across its own output ─────────────────────────────
    //
    //   With G=0 the source contributes nothing at all, so a 5 V divider reading 10/3 V at n2 is
    //   unchanged by a VCCS hung across its lower leg. An ideal current source has infinite output
    //   impedance; a stamp that put conductance on the output diagonal would load the divider.
    [Fact]
    public void Vccs_WithZeroG_IsAnOpenCircuit()
    {
        var (nl, dc) = RunDc(@"
Vdc:V1    n1 0        Vdc=5 V
R:R1      n1 n2       R=100 Ohm
R:R2      n2 0        R=200 Ohm
VCCS:G1   n2 0 n1 0   G=0

analysis DC1  type=dc
");
        Assert.True(dc.Converged);
        Assert.Equal(10.0 / 3.0, V(nl, dc, "n2"), Tol);
    }

    // ── VCCS in S-parameters: a unilateral amplifier with a known S21 ─────────
    //
    //   Port 1 (50 Ω) → nin, terminated by Rin = 50 Ω; VCCS senses nin and drives nout, loaded by
    //   RL = 50 Ω and Port 2 (50 Ω).
    //     a1 = 1 ⟹ V(nin) = 1 (half of the 2 V open-circuit source into a matched 50/50 divider,
    //     in the engine's own wave normalisation) — rather than assume that, the test asserts the
    //     RATIO S21/S11 arithmetic that holds for any consistent normalisation:
    //       S11 = 0 exactly (Rin = Z0, and the control pair loads nothing).
    //       S21 = −G · (RL ∥ Z0) = −10 mS · 25 Ω = −0.25 — the standard inverting unilateral
    //             result, NEGATIVE because the source draws its current out of out+ (down through
    //             the device). The sign is the whole reason this assertion is here: the magnitude
    //             alone passes under either direction.
    //     S12 = 0 exactly — nothing carries a signal backwards through a control pair that draws no
    //             current, which is the unilaterality this device is usually placed for.
    [Fact]
    public void Vccs_SParam_IsUnilateral_WithTheTextbookTransconductanceGain()
    {
        var (lib, tb) = new CnlReader().Read(@"
Port:P1   nin  0   Num=1  Z=50 Ohm
R:Rin     nin  0   R=50 Ohm
VCCS:G1   nout 0 nin 0   G=0.01
R:RL      nout 0   R=50 Ohm
Port:P2   nout 0   Num=2  Z=50 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);
        Complex s11 = (Complex)ds["S"][0, 0, 0];
        Complex s21 = (Complex)ds["S"][0, 1, 0];
        Complex s12 = (Complex)ds["S"][0, 0, 1];
        output.WriteLine($"S11={s11}  S21={s21}  S12={s12}");

        Assert.True(s11.Magnitude < 1e-12, $"S11 should be 0 (matched 50 Ω input), got {s11}");
        Assert.True(s12.Magnitude < 1e-12, $"S12 should be 0 (a VCCS is unilateral), got {s12}");
        Assert.Equal(-0.25, s21.Real, 9);
        Assert.Equal(0.0,   s21.Imaginary, 9);
    }

    // ── A VCCS with too few nets is refused, by name ──────────────────────────
    [Fact]
    public void Vccs_WithThreeNets_IsRefusedAndNamesTheInstance()
    {
        var (lib, tb) = new CnlReader().Read(@"
Vdc:V1    nc 0     Vdc=2 V
VCCS:G1   n_out 0 nc   G=0.01
R:RL      n_out 0  R=100 Ohm

analysis DC1  type=dc
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var ex = Assert.ThrowsAny<Exception>(() => NonlinearDcEngine.Run(nl));
        Assert.Contains("G1", ex.Message, StringComparison.Ordinal);
        output.WriteLine(ex.Message);
    }
}
