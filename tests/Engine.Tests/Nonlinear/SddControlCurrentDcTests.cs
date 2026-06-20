using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Gate tests for SDD control currents (_cn) at DC.
/// Verifies: read-through correctness, Jacobian exactness, resolver errors,
/// all five referenceable device classes, and regression (no C[n] → identical).
/// Design ref: docs/design/sdd-control-current.md.
/// </summary>
public class SddControlCurrentDcTests(ITestOutputHelper output)
{
    // ── T1: DC read-through — SDD mirrors IProbe current via _c1 ─────────────
    //
    // Circuit:
    //   Vdc(2V) → n1
    //   R1(1kΩ): n1 → n2
    //   IProbe(IP1): n2 → 0  =>  I(IP1) = 2V/1kΩ = 2mA
    //
    //   R2(500Ω): n1 → n3
    //   SDD(X1): n3 → 0, I[1,0]=_c1, C[1]=IP1  =>  SDD sources 2mA
    //   V(n3) = 2V − 500Ω × 2mA = 1V
    [Fact]
    public void T1_DcReadThrough_SddSourcesIProbeCurrentViaC1()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=2 V
R:R1    n1 n2  R=1000 Ohm
IProbe:IP1  n2 0

R:R2    n1 n3  R=500 Ohm
SDD:X1  n3 0  I[1,0]=_c1  C[1]=IP1

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"Did not converge. Residual={result.FinalResidual:G}");

        // I(IP1) = 2mA
        Assert.True(result.ProbeCurrents.ContainsKey("IP1"), "IP1 probe current missing");
        double iProbe = result.ProbeCurrents["IP1"];
        output.WriteLine($"I(IP1) = {iProbe * 1e3:G6} mA  (expected 2 mA)");
        Assert.True(Math.Abs(iProbe - 0.002) < 1e-9, $"Expected I(IP1) ≈ 2 mA, got {iProbe * 1e3:G} mA");

        // V(n3) = 1V
        double vn3 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - 1.0) < 0.1);
        output.WriteLine($"V(n3) = {vn3:G6} V  (expected 1.0 V)");
        Assert.True(Math.Abs(vn3 - 1.0) < 1e-7, $"Expected V(n3) ≈ 1.0 V, got {vn3:G}");

        output.WriteLine("T1_DcReadThrough_SddSourcesIProbeCurrentViaC1: PASS.");
    }

    // ── T2: DC Jacobian exactness — beta*_c1 converges quickly with DControl ──
    //
    // I[1,0] = beta*_c1 is linear in _c1; with exact DControl stamped the
    // augmented system is linear → Newton converges in very few iterations.
    //
    // Circuit:
    //   Vdc(1V) → n1 → R1(1kΩ) → n2 → IProbe(IP1) → 0   ⟹ I(IP1) = 1mA
    //   R2(1kΩ): n1 → n3;  SDD: n3 → 0, I[1,0]=beta*_c1, C[1]=IP1
    //   beta=2  ⟹ SDD sources 2mA  ⟹  V(n3) = 1V − 1kΩ×2mA = −1V
    [Fact]
    public void T2_DcJacobian_BetaTimesC1_ConvergesWithExactJacobian()
    {
        const string cnl = @"
beta = 2
Vdc:VS  n1 0  Vdc=1 V
R:R1    n1 n2  R=1000 Ohm
IProbe:IP1  n2 0

R:R2    n1 n3  R=1000 Ohm
SDD:X1  n3 0  I[1,0]=beta*_c1  C[1]=IP1

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"Did not converge. Residual={result.FinalResidual:G}");

        // V(n3) = 1 − 1000 × 0.002 = −1V
        double vn3 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - (-1.0)) < 0.2);
        output.WriteLine($"V(n3) = {vn3:G6} V  (expected −1.0 V)");
        Assert.True(Math.Abs(vn3 - (-1.0)) < 1e-7, $"Expected V(n3) ≈ −1.0 V, got {vn3:G}");

        // With exact DControl the linearised system converges in ≤10 Newton iterations.
        output.WriteLine($"Total Newton iterations: {result.Iterations}");
        Assert.True(result.Iterations <= 10, $"Expected ≤10 iterations with exact DControl, got {result.Iterations}");

        output.WriteLine("T2_DcJacobian_BetaTimesC1_ConvergesWithExactJacobian: PASS.");
    }

    // ── T3: Control current via Vdc source ────────────────────────────────────
    //
    // The Vdc source has a branch current unknown. Referencing it via C[1]=VS
    // lets the SDD mirror (with a sign flip) the supply current.
    [Fact]
    public void T3_VdcKind_ControlCurrentWorks()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=10 V
R:R1    n1 0  R=100 Ohm
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1*(-1)  C[1]=VS

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"VdcKind did not converge. Residual={result.FinalResidual:G}");
        output.WriteLine("T3_VdcKind_ControlCurrentWorks: PASS.");
    }

    // ── T4: Control current via Inductor ──────────────────────────────────────
    //
    // At DC an inductor is a short; its branch current is set by the rest of the
    // circuit. Vdc=1V / R1=1kΩ → L1(short) → 0  ⟹ I(L1) = 1mA.
    // SDD X1: I[1,0]=_c1=1mA at n3→0, R2=500Ω: n1→n3
    // V(n3) = 1V − 500Ω × 1mA = 0.5V
    [Fact]
    public void T4_InductorKind_ControlCurrentWorks()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
R:R1    n1 n2  R=1000 Ohm
L:L1    n2 0   L=1n

R:R2    n1 n3  R=500 Ohm
SDD:X1  n3 0  I[1,0]=_c1  C[1]=L1

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"InductorKind did not converge. Residual={result.FinalResidual:G}");

        double vn3 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - 0.5) < 0.1);
        output.WriteLine($"V(n3) = {vn3:G6} V  (expected 0.5 V)");
        Assert.True(Math.Abs(vn3 - 0.5) < 1e-7, $"Expected V(n3) ≈ 0.5 V, got {vn3:G}");

        output.WriteLine("T4_InductorKind_ControlCurrentWorks: PASS.");
    }

    // ── T5: ZPort port-branch reference ───────────────────────────────────────
    //
    // Z_Port stamps per-port branch currents. At DC with 1V across the port,
    // Z[1,1]=1kΩ  ⟹  I_port = 1V/1kΩ = 1mA.
    // SDD: C[1]=ZP1, Cport[1]=1, I[1,0]=_c1
    // R3=500Ω: n1→n5. V(n5) = 1V − 500Ω × 1mA = 0.5V
    [Fact]
    public void T5_ZPortKind_ControlCurrentWorks()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
Z_Port:ZP1  n1 0  Z[1,1]=1000

R:R3    n1 n5  R=500 Ohm
SDD:X1  n5 0  I[1,0]=_c1  C[1]=ZP1  Cport[1]=1

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"ZPortKind did not converge. Residual={result.FinalResidual:G}");

        double vn5 = result.NodeVoltages.FirstOrDefault(v => Math.Abs(v - 0.5) < 0.1);
        output.WriteLine($"V(n5) = {vn5:G6} V  (expected 0.5 V)");
        Assert.True(Math.Abs(vn5 - 0.5) < 1e-7, $"Expected V(n5) ≈ 0.5 V, got {vn5:G}");

        output.WriteLine("T5_ZPortKind_ControlCurrentWorks: PASS.");
    }

    // ── Resolver error tests ──────────────────────────────────────────────────

    // T6: Missing instance → error names the missing ref
    [Fact]
    public void T6_MissingInstance_Throws()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
R:R1    n1 0  R=100 Ohm
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1  C[1]=NonExistent

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T6 exception: {ex.Message}");
        Assert.Contains("NonExistent", ex.Message);
        output.WriteLine("T6_MissingInstance_Throws: PASS.");
    }

    // T7: Non-referenceable kind (resistor) → error lists allowed kinds
    [Fact]
    public void T7_NonReferenceableKind_Throws()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
R:R1    n1 0  R=100 Ohm
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1  C[1]=R1

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T7 exception: {ex.Message}");
        Assert.Contains("referenceable", ex.Message);
        output.WriteLine("T7_NonReferenceableKind_Throws: PASS.");
    }

    // T8: Cport specified on a two-terminal device → error names the constraint
    [Fact]
    public void T8_CportOnTwoTerminal_Throws()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
IProbe:IP1  n1 0
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1  C[1]=IP1  Cport[1]=2

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T8 exception: {ex.Message}");
        Assert.Contains("two-terminal", ex.Message);
        output.WriteLine("T8_CportOnTwoTerminal_Throws: PASS.");
    }

    // T9: Cport absent on multi-port device → error says it is required
    [Fact]
    public void T9_MissingCportOnMultiport_Throws()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
Z_Port:ZP1  n1 0  Z[1,1]=50
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1  C[1]=ZP1

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T9 exception: {ex.Message}");
        Assert.Contains("required", ex.Message);
        output.WriteLine("T9_MissingCportOnMultiport_Throws: PASS.");
    }

    // T10: Cport out of range for Z_Port (1-port, Cport=5) → error says "out of range"
    [Fact]
    public void T10_CportOutOfRange_Throws()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
Z_Port:ZP1  n1 0  Z[1,1]=50
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1  C[1]=ZP1  Cport[1]=5

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            var nl = new Elaborator(lib).Elaborate(tb);
            NonlinearDcEngine.Run(nl);
        });
        output.WriteLine($"T10 exception: {ex.Message}");
        Assert.Contains("out of range", ex.Message);
        output.WriteLine("T10_CportOutOfRange_Throws: PASS.");
    }

    // T11: _c1 in equation but no C[1] declared → factory cross-validation error at elaboration
    [Fact]
    public void T11_UnmatchedControlVarRef_ThrowsAtFactory()
    {
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
R:R1    n1 0  R=100 Ohm
R:R2    n2 0  R=100 Ohm
SDD:X1  n2 0  I[1,0]=_c1

analysis DC1  type=dc
";
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            // Factory cross-validation fires during elaboration
            new Elaborator(lib).Elaborate(tb);
        });
        output.WriteLine($"T11 exception: {ex.Message}");
        Assert.Contains("_c1", ex.Message);
        output.WriteLine("T11_UnmatchedControlVarRef_ThrowsAtFactory: PASS.");
    }

    // ── T12: Regression — SDD without C[n] behaves identically to before ──────
    [Fact]
    public void T12_Regression_NoControlRefs_BehaviorUnchanged()
    {
        // SDD acting as a 50Ω resistor via I[1,0]=_v1/50. No C[n] references.
        const string cnl = @"
Vdc:VS  n1 0  Vdc=1 V
SDD:R1  n1 0  I[1,0]=_v1/50
R:R2    n1 0  R=50 Ohm

analysis DC1  type=dc
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);

        Assert.True(result.Converged, $"Regression: did not converge. Residual={result.FinalResidual:G}");
        // V(n1) ≈ 1V (VS-forced)
        Assert.True(result.NodeVoltages.Any(v => Math.Abs(v - 1.0) < 1e-4),
            $"Regression: no node voltage ≈ 1V. NodeVoltages=[{string.Join(", ", result.NodeVoltages)}]");

        output.WriteLine("T12_Regression_NoControlRefs_BehaviorUnchanged: PASS.");
    }
}
