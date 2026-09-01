using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// SRLC and PRLC — the two lumped RLC parts.
///
/// <para><b>The oracle is a closed-form impedance, not another circuitRF path.</b> Each shunt case
/// below computes the element's own Z(ω) by hand and turns it into an S11 with the textbook
/// one-port reflection formula. Comparing an SRLC against three wired elements would be the cheaper
/// test to write and a much weaker one: the two paths share the whole engine underneath, so a sign
/// error in the constraint diagonal would agree with itself. Where an equivalence IS asserted (the
/// SRLC-versus-L-with-R-and-C case) it is because that equivalence is the specific claim being
/// made — the two stamp identical arithmetic on purpose — and it is asserted ALONGSIDE the closed
/// form, never instead of it.</para>
/// </summary>
public class SeriesParallelRlcTests
{
    private static DataSet Run(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz);
    }

    private static Complex Sij(DataSet ds, int r, int c, int fi = 0) => (Complex)ds["S"][fi, r, c];

    /// <summary>S11 of a one-port shunt impedance Z on a Z0 reference: (Z − Z0)/(Z + Z0).</summary>
    private static Complex ShuntS11(Complex z, double z0) => (z - z0) / (z + z0);

    private const double Z0 = 50.0;

    // ── SRLC: the series branch ──────────────────────────────────────────────

    [Fact]
    public void Srlc_Shunt_MatchesClosedFormSeriesImpedance()
    {
        const double R = 2.0, L = 3e-9, C = 4e-12, F = 1.7e9;
        double w = 2 * Math.PI * F;

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
SRLC:X1  n1 0  R=2 Ohm  L=3 nH  C=4 pF
", [F]);

        var z = new Complex(R, w * L - 1.0 / (w * C));
        Assert.True((Sij(ds, 0, 0) - ShuntS11(z, Z0)).Magnitude < 1e-9,
            $"S11={Sij(ds, 0, 0):G6} expected {ShuntS11(z, Z0):G6}");
    }

    /// <summary>
    /// At its own series resonance the branch is purely resistive — the whole reason a designer
    /// reaches for this part. 1 nH with 1 pF resonates at 1/(2π√LC) ≈ 5.0329 GHz, where Z = R
    /// exactly and S11 is real.
    /// </summary>
    [Fact]
    public void Srlc_AtSeriesResonance_IsPurelyResistive()
    {
        const double R = 5.0, L = 1e-9, C = 1e-12;
        double f0 = 1.0 / (2 * Math.PI * Math.Sqrt(L * C));

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
SRLC:X1  n1 0  R=5 Ohm  L=1 nH  C=1 pF
", [f0]);

        var s11 = Sij(ds, 0, 0);
        Assert.True(Math.Abs(s11.Imaginary) < 1e-9, $"S11 imag={s11.Imaginary:G6}, expected 0");
        Assert.True(Math.Abs(s11.Real - (R - Z0) / (R + Z0)) < 1e-9,
            $"S11 real={s11.Real:G6}, expected {(R - Z0) / (R + Z0):G6}");
    }

    /// <summary>
    /// The series capacitance opens the branch at DC, so a shunt SRLC leaves the port open —
    /// S11 = +1. The engine reaches ω = 0 on every HB run (the DC harmonic), so this is the path
    /// that gets exercised in practice, not a corner case.
    /// </summary>
    [Fact]
    public void Srlc_AtDc_IsAnOpenCircuit()
    {
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
SRLC:X1  n1 0  R=2 Ohm  L=3 nH  C=4 pF
", [0.0]);

        Assert.True((Sij(ds, 0, 0) - Complex.One).Magnitude < 1e-9,
            $"S11 at DC = {Sij(ds, 0, 0):G6}, expected +1 (open)");
    }

    /// <summary>
    /// An SRLC and an <c>L</c> carrying the same optional R= and C= are the same branch. The
    /// equivalence is deliberate — SRLC exists for the symbol, the parameter set and the netlist
    /// spelling, not for different arithmetic — and pinning it here is what stops the two drifting
    /// apart if either stamp is ever edited.
    /// </summary>
    [Fact]
    public void Srlc_IsTheSameBranchAsAnInductorCarryingRandC()
    {
        double[] fs = [1e8, 1e9, 6e9];
        const string tail = @"
Port:P2  n2 0  Num=2 Z=50 Ohm
R:Rsh  n2 0  R=200 Ohm
";
        var srlc = Run("Port:P1  n1 0  Num=1 Z=50 Ohm\nSRLC:X1  n1 n2  R=2 Ohm L=3 nH C=4 pF\n" + tail, fs);
        var ind  = Run("Port:P1  n1 0  Num=1 Z=50 Ohm\nL:X1     n1 n2  R=2 Ohm L=3 nH C=4 pF\n" + tail, fs);

        for (int fi = 0; fi < fs.Length; fi++)
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
            Assert.True((Sij(srlc, r, c, fi) - Sij(ind, r, c, fi)).Magnitude < 1e-12,
                $"f={fs[fi]:G3} S[{r + 1},{c + 1}]: SRLC={Sij(srlc, r, c, fi):G6} L={Sij(ind, r, c, fi):G6}");
    }

    // ── PRLC: the tank ───────────────────────────────────────────────────────

    [Fact]
    public void Prlc_Shunt_MatchesClosedFormParallelAdmittance()
    {
        const double R = 300.0, L = 5e-9, C = 2e-12, F = 1.3e9;
        double w = 2 * Math.PI * F;

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
PRLC:X1  n1 0  R=300 Ohm  L=5 nH  C=2 pF
", [F]);

        var y = new Complex(1.0 / R, w * C - 1.0 / (w * L));
        Assert.True((Sij(ds, 0, 0) - ShuntS11(1.0 / y, Z0)).Magnitude < 1e-9,
            $"S11={Sij(ds, 0, 0):G6} expected {ShuntS11(1.0 / y, Z0):G6}");
    }

    /// <summary>At parallel resonance the tank is purely resistive at R — its whole purpose.</summary>
    [Fact]
    public void Prlc_AtParallelResonance_IsPurelyResistive()
    {
        const double R = 400.0, L = 2e-9, C = 5e-12;
        double f0 = 1.0 / (2 * Math.PI * Math.Sqrt(L * C));

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
PRLC:X1  n1 0  R=400 Ohm  L=2 nH  C=5 pF
", [f0]);

        var s11 = Sij(ds, 0, 0);
        Assert.True(Math.Abs(s11.Imaginary) < 1e-9, $"S11 imag={s11.Imaginary:G6}, expected 0");
        Assert.True(Math.Abs(s11.Real - (R - Z0) / (R + Z0)) < 1e-9,
            $"S11 real={s11.Real:G6}, expected {(R - Z0) / (R + Z0):G6}");
    }

    /// <summary>
    /// The ideal inductor shorts the tank at DC, so a shunt PRLC grounds the port — S11 = −1.
    /// The branch's zero diagonal is what produces that exactly, with no gmin involved.
    /// </summary>
    [Fact]
    public void Prlc_AtDc_IsAShortCircuit()
    {
        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
PRLC:X1  n1 0  R=300 Ohm  L=5 nH  C=2 pF
", [0.0]);

        Assert.True((Sij(ds, 0, 0) + Complex.One).Magnitude < 1e-9,
            $"S11 at DC = {Sij(ds, 0, 0):G6}, expected −1 (short)");
    }

    /// <summary>
    /// A PRLC is R ∥ L ∥ C — the same three elements wired in parallel by hand. Unlike the SRLC
    /// case above this is NOT a shared stamp (the discrete form has no branch for C and takes a
    /// different path for L), so the agreement is a real cross-check of the topology.
    /// </summary>
    [Fact]
    public void Prlc_IsTheSameNetworkAsThreeDiscreteElementsInParallel()
    {
        double[] fs = [1e8, 1e9, 6e9];
        const string ports = "Port:P1  n1 0  Num=1 Z=50 Ohm\nPort:P2  n2 0  Num=2 Z=50 Ohm\nR:Rs  n1 n2  R=25 Ohm\n";

        var lumped   = Run(ports + "PRLC:X1  n2 0  R=300 Ohm L=5 nH C=2 pF\n", fs);
        var discrete = Run(ports + "R:Rp  n2 0  R=300 Ohm\nL:Lp  n2 0  L=5 nH\nC:Cp  n2 0  C=2 pF\n", fs);

        for (int fi = 0; fi < fs.Length; fi++)
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
            Assert.True((Sij(lumped, r, c, fi) - Sij(discrete, r, c, fi)).Magnitude < 1e-10,
                $"f={fs[fi]:G3} S[{r + 1},{c + 1}]: PRLC={Sij(lumped, r, c, fi):G6} discrete={Sij(discrete, r, c, fi):G6}");
    }

    // ── Mutual coupling to an SRLC / PRLC inductor ───────────────────────────

    /// <summary>
    /// A Mutual couples to an SRLC's inductor. The oracle is the coupled-branch solution written
    /// out by hand: two shunt SRLCs on separate ports, coupled by M, give
    /// Z11 = Z22 = Z_srlc and Z21 = Z12 = jωM, so the S-matrix follows from the 2×2 Z-to-S formula.
    /// The transmission through the coupling is the whole point — an M that silently failed to
    /// resolve would leave S21 at zero, which this catches.
    /// </summary>
    [Fact]
    public void Mutual_CouplesToAnSrlcInductor()
    {
        const double R = 2.0, L = 8e-9, C = 4e-12, M = 3e-9, F = 1.1e9;
        double w = 2 * Math.PI * F;

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
SRLC:X1  n1 0  R=2 Ohm  L=8 nH  C=4 pF
SRLC:X2  n2 0  R=2 Ohm  L=8 nH  C=4 pF
Mutual:M12  M=3 nH  Inductor1=""X1""  Inductor2=""X2""
", [F]);

        var z11 = new Complex(R, w * L - 1.0 / (w * C));
        var z21 = new Complex(0, w * M);
        AssertMatchesTwoPortZ(ds, z11, z21);
    }

    /// <summary>
    /// The same coupling through a PRLC's inductor branch. The Z-matrix here is not a hand-written
    /// series impedance — the coupled pair is R ∥ C across a coupled inductor pair — so the oracle
    /// is built from the port admittance of that combination rather than reused from the SRLC case.
    /// </summary>
    [Fact]
    public void Mutual_CouplesToAPrlcInductor()
    {
        const double R = 400.0, L = 8e-9, C = 2e-12, M = 3e-9, F = 1.1e9;
        double w = 2 * Math.PI * F;

        var ds = Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
PRLC:X1  n1 0  R=400 Ohm  L=8 nH  C=2 pF
PRLC:X2  n2 0  R=400 Ohm  L=8 nH  C=2 pF
Mutual:M12  M=3 nH  Inductor1=""X1""  Inductor2=""X2""
", [F]);

        // The two coupled inductors alone form a 2×2 impedance block [jωL, jωM; jωM, jωL]; its
        // inverse is the inductive admittance block, to which each port adds its own 1/R + jωC.
        var jwL = new Complex(0, w * L);
        var jwM = new Complex(0, w * M);
        var det = jwL * jwL - jwM * jwM;
        var yInd11 =  jwL / det;
        var yInd21 = -jwM / det;

        var yShunt = new Complex(1.0 / R, w * C);
        var y11 = yInd11 + yShunt;
        var y21 = yInd21;

        // Y → Z for a symmetric 2×2.
        var dY = y11 * y11 - y21 * y21;
        AssertMatchesTwoPortZ(ds, y11 / dY, -y21 / dY);
    }

    /// <summary>
    /// A Mutual pointed at something with no inductor branch is refused, and the refusal says which
    /// kinds DO work. The old message named the internal class ("is not an InductorModel"), which
    /// left a user who had pointed a Mutual at a resistor with nothing actionable.
    /// </summary>
    [Fact]
    public void Mutual_ReferencingANonInductiveComponent_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Run(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 0  R=50 Ohm
L:L1  n1 0  L=1 nH
Mutual:M12  M=0.1 nH  Inductor1=""R1""  Inductor2=""L1""
", [1e9]));

        Assert.Contains("SRLC", ex.Message);
        Assert.Contains("PRLC", ex.Message);
    }

    /// <summary>Symmetric two-port: compare the simulated S against Z11/Z21 through Z → S.</summary>
    private static void AssertMatchesTwoPortZ(DataSet ds, Complex z11, Complex z21)
    {
        // Z → S for a symmetric, reciprocal two-port on a uniform real Z0.
        var d   = (z11 + Z0) * (z11 + Z0) - z21 * z21;
        var s11 = ((z11 - Z0) * (z11 + Z0) - z21 * z21) / d;
        var s21 = (2.0 * z21 * Z0) / d;

        Assert.True((Sij(ds, 0, 0) - s11).Magnitude < 1e-9, $"S11={Sij(ds, 0, 0):G6} expected {s11:G6}");
        Assert.True((Sij(ds, 1, 1) - s11).Magnitude < 1e-9, $"S22={Sij(ds, 1, 1):G6} expected {s11:G6}");
        Assert.True((Sij(ds, 1, 0) - s21).Magnitude < 1e-9, $"S21={Sij(ds, 1, 0):G6} expected {s21:G6}");
        Assert.True((Sij(ds, 0, 1) - s21).Magnitude < 1e-9, $"S12={Sij(ds, 0, 1):G6} expected {s21:G6}");
    }
}
