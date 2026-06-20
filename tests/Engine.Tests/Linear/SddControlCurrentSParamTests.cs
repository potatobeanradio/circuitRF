using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gate tests for the SDD control-current (_cn) column in SParameterEngine.StampLinearized
/// (brief #4 — design §5). Verifies the control coupling reduces to the right admittance, the
/// sign matches the DC engine at ω→0, all five referenceable kinds resolve and stay non-singular,
/// unresolved references error clearly, and control-free runs are unchanged.
/// </summary>
public class SddControlCurrentSParamTests(ITestOutputHelper output)
{
    private static (ElaboratedNetlist Netlist, DataSet Result) Run(
        string cnl, double[] freqsHz, AnalysisSettings? settings = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, freqsHz, settings);
        return (nl, ds);
    }

    private static Complex S(DataSet ds, int r, int c, int fi = 0) => (Complex)ds["S"][fi, r, c];

    private static string Hero1Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "Hero1");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1 not found");
    }

    // ── T1: control column reduces to a known admittance (equivalence to a built-in) ──────────
    //
    //   Port P1 at n1 (Z0=50).
    //   IProbe IP1: n1 → n2 ;  R Rref: n2 → 0      ⟹  _c1 = I(IP1) = V(n1)/Rref
    //   SDD D1: n1 → 0, I[1] = beta*_c1            ⟹  SDD draws beta·V(n1)/Rref into n1.
    //
    //   Total admittance at n1 = 1/Rref (the IP1+Rref branch) + beta/Rref (control coupling)
    //                          = (1+beta)/Rref  ⟹  Zeq = Rref/(1+beta).
    //   With Rref=100, beta=3 → Zeq = 25 Ω → S11 = (25−50)/(25+50) = −1/3.
    //   Reference: a plain 25 Ω shunt gives the same S11.
    [Fact]
    public void T1_ControlColumn_ReducesToKnownAdmittance()
    {
        const string cnlSdd = """
            Port:P1     n1 0   Num=1  Z=50 Ohm
            IProbe:IP1  n1 n2
            R:Rref      n2 0   R=100 Ohm
            SDD:D1      n1 0   I[1]=3*_c1  C[1]=IP1
            """;
        const string cnlRef = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:Req    n1 0  R=25 Ohm
            """;

        var (_, dsSdd) = Run(cnlSdd, [1e9, 2e9]);
        var (_, dsRef) = Run(cnlRef, [1e9, 2e9]);

        const double tol = 1e-6;
        for (int fi = 0; fi < 2; fi++)
        {
            var sSdd = S(dsSdd, 0, 0, fi);
            var sRef = S(dsRef, 0, 0, fi);
            output.WriteLine($"fi={fi}: S11(SDD)={sSdd:G6}  S11(25Ω)={sRef:G6}");
            Assert.True((sSdd - sRef).Magnitude < tol,
                $"S11 mismatch at fi={fi}: SDD={sSdd:G6}, ref={sRef:G6}");
        }

        // Analytic check: S11 = −1/3 (real, frequency-independent — purely resistive coupling).
        var s11 = S(dsSdd, 0, 0, 0);
        Assert.True(Math.Abs(s11.Real - (-1.0 / 3.0)) < 1e-5, $"S11.Real={s11.Real} (expected −1/3)");
        Assert.True(Math.Abs(s11.Imaginary) < 1e-5, $"S11.Imag={s11.Imaginary} (expected 0)");
        output.WriteLine("T1_ControlColumn_ReducesToKnownAdmittance: PASS.");
    }

    // ── T2: sign + DC agreement at ω→0 ────────────────────────────────────────────────────────
    //
    // Same topology as T1. A POSITIVE beta lowers Zeq (more admittance drawn) → S11 more negative;
    // a NEGATIVE beta raises Zeq → S11 more positive. This pins Problem 1's sign convention (the
    // control column must add +col at the +node, matching the DC engine's +DControl), and the value
    // at ω→0 is exactly the DC conductance (1+beta)/Rref.
    [Fact]
    public void T2_ControlColumn_SignAndDcAgreementAtLowFreq()
    {
        string Cnl(string beta) => $"""
            Port:P1     n1 0   Num=1  Z=50 Ohm
            IProbe:IP1  n1 n2
            R:Rref      n2 0   R=100 Ohm
            SDD:D1      n1 0   I[1]={beta}*_c1  C[1]=IP1
            """;

        // ω→0 probe frequency.
        var (_, dsPos)  = Run(Cnl("3"),    [1.0]);
        var (_, dsNeg)  = Run(Cnl("-0.5"), [1.0]);
        var (_, dsZero) = Run(Cnl("0"),    [1.0]);   // no coupling → just the 100 Ω branch

        double s11Pos  = S(dsPos,  0, 0).Real;   // (1+β)=4   → Zeq = 25  → −1/3
        double s11Neg  = S(dsNeg,  0, 0).Real;   // (1+β)=0.5 → Zeq = 200 → +0.6
        double s11Zero = S(dsZero, 0, 0).Real;   // (1+β)=1   → Zeq = 100 → +1/3

        output.WriteLine($"S11: beta=+3 → {s11Pos:G6}, beta=0 → {s11Zero:G6}, beta=−0.5 → {s11Neg:G6}");

        // Sign monotonicity: more positive beta ⇒ more admittance ⇒ lower Zeq ⇒ more negative S11.
        Assert.True(s11Pos < s11Zero && s11Zero < s11Neg,
            $"Expected S11(+3) < S11(0) < S11(−0.5): {s11Pos} < {s11Zero} < {s11Neg}");

        // Quantitative DC agreement: the ω→0 admittance equals the DC conductance (1+beta)/Rref.
        Assert.True(Math.Abs(s11Pos  - (-1.0 / 3.0)) < 1e-6, $"S11(beta=+3)={s11Pos} (expected −1/3)");
        Assert.True(Math.Abs(s11Zero - ( 1.0 / 3.0)) < 1e-6, $"S11(beta=0)={s11Zero} (expected +1/3)");
        Assert.True(Math.Abs(s11Neg  - 0.6) < 1e-6,          $"S11(beta=−0.5)={s11Neg} (expected +0.6)");
        output.WriteLine("T2_ControlColumn_SignAndDcAgreementAtLowFreq: PASS.");
    }

    // ── T3: charge-path control coupling produces a reactive (jω) admittance ──────────────────
    //
    // Q[1] = tau*_c1 (a charge that tracks the referenced current) contributes jω·tau/Rref to the
    // admittance at n1 via DControlCharge. So Y(n1) = (1 + jω·tau)/Rref and S11 becomes frequency
    // dependent with a non-zero imaginary part — proving the w=1 (Weight(1,ω)=jω) control term is wired.
    [Fact]
    public void T3_ChargeControlColumn_IsReactive()
    {
        const string cnl = """
            Port:P1     n1 0   Num=1  Z=50 Ohm
            IProbe:IP1  n1 n2
            R:Rref      n2 0   R=100 Ohm
            SDD:D1      n1 0   Q[1]=1e-9*_c1  C[1]=IP1
            """;

        var (_, ds) = Run(cnl, [1e6, 1e9]);
        var sLo = S(ds, 0, 0, 0);
        var sHi = S(ds, 0, 0, 1);
        output.WriteLine($"S11 @1MHz={sLo:G6}, @1GHz={sHi:G6}");

        // Reactive term grows with frequency → |Imag(S11)| at 1 GHz ≫ at 1 MHz.
        Assert.True(Math.Abs(sHi.Imaginary) > 1e-3, $"Expected reactive S11 at 1 GHz, got {sHi}");
        Assert.True(Math.Abs(sHi.Imaginary) > 100 * Math.Abs(sLo.Imaginary),
            $"Reactance should scale with ω: lo={sLo.Imaginary:G3}, hi={sHi.Imaginary:G3}");
        output.WriteLine("T3_ChargeControlColumn_IsReactive: PASS.");
    }

    // ── T4: all five referenceable kinds resolve and stay non-singular ────────────────────────

    [Theory]
    [InlineData("Vdc")]
    [InlineData("IProbe")]
    [InlineData("Inductor")]
    [InlineData("ZPort")]
    [InlineData("Snp")]
    public void T4_AllFiveKinds_ResolveAndAreNonSingular(string kind)
    {
        string refDecl, cDecl;
        switch (kind)
        {
            case "Vdc":      refDecl = "Vdc:VS  nx 0  Vdc=1 V";          cDecl = "C[1]=VS"; break;
            case "IProbe":   refDecl = "IProbe:IP1  nx 0";              cDecl = "C[1]=IP1"; break;
            case "Inductor": refDecl = "L:L1  nx 0  L=1n";              cDecl = "C[1]=L1"; break;
            case "ZPort":    refDecl = "Z_Port:ZP1  nx 0  Z[1,1]=50";   cDecl = "C[1]=ZP1  Cport[1]=1"; break;
            case "Snp":
                var snp = Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p");
                refDecl = $"SnP:SP1  nx ny  NumPorts=2 File=\"{snp}\" Type=\"touchstone\" InterpMode=\"linear\" ExtrapMode=\"clamp\"";
                cDecl   = "C[1]=SP1  Cport[1]=1";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        // ny only matters for the SnP (2-port); harmless extra net otherwise.
        string cnl = $"""
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:Rin    n1 nx  R=10 Ohm
            R:Rterm  ny 0   R=50 Ohm
            {refDecl}
            SDD:D1   n1 0   I[1]=_v1/200 + 0.5*_c1  {cDecl}
            """;

        var (nl, ds) = Run(cnl, [1e9]);
        var s11 = S(ds, 0, 0);
        output.WriteLine($"kind={kind}: S11={s11:G6}");

        // Non-singular run: finite S, control column active (no resolver error thrown).
        Assert.False(double.IsNaN(s11.Real) || double.IsNaN(s11.Imaginary), $"S11 not finite for {kind}");
        Assert.True(s11.Magnitude <= 1.0 + 1e-6, $"|S11|={s11.Magnitude} > 1 for {kind} (non-physical)");
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("singular", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"T4_AllFiveKinds[{kind}]: PASS.");
    }

    // ── T5: non-referenceable / missing reference → clear error ───────────────────────────────

    [Fact]
    public void T5_NonReferenceableKind_Throws()
    {
        // _c1 references a resistor — not a branch-current device.
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1     n1 n2  R=100 Ohm
            R:R2     n2 0   R=100 Ohm
            SDD:D1   n1 0   I[1]=_c1  C[1]=R1
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Run(cnl, [1e9]));
        output.WriteLine($"T5 exception: {ex.Message}");
        Assert.Contains("referenceable", ex.Message);
        output.WriteLine("T5_NonReferenceableKind_Throws: PASS.");
    }

    [Fact]
    public void T5b_MissingInstance_Throws()
    {
        const string cnl = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1     n1 0  R=100 Ohm
            SDD:D1   n1 0   I[1]=_c1  C[1]=NoSuchDevice
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Run(cnl, [1e9]));
        output.WriteLine($"T5b exception: {ex.Message}");
        Assert.Contains("NoSuchDevice", ex.Message);
        output.WriteLine("T5b_MissingInstance_Throws: PASS.");
    }

    // ── T6: regression — an SDD without C[n] is byte-identical to the equivalent built-in ─────

    [Fact]
    public void T6_Regression_NoControlRefs_Unchanged()
    {
        // SDD acting as a 75 Ω shunt via I[1]=_v1/75. No control references → no column stamped.
        const string cnlSdd = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            SDD:D1   n1 0  I[1]=_v1/75
            """;
        const string cnlRef = """
            Port:P1  n1 0  Num=1  Z=50 Ohm
            R:R1     n1 0  R=75 Ohm
            """;

        var (_, dsSdd) = Run(cnlSdd, [1e9, 2e9]);
        var (_, dsRef) = Run(cnlRef, [1e9, 2e9]);
        for (int fi = 0; fi < 2; fi++)
        {
            var sSdd = S(dsSdd, 0, 0, fi);
            var sRef = S(dsRef, 0, 0, fi);
            Assert.True((sSdd - sRef).Magnitude < 1e-6, $"S11 mismatch at fi={fi}: {sSdd} vs {sRef}");
        }
        output.WriteLine("T6_Regression_NoControlRefs_Unchanged: PASS.");
    }
}
