using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Tests for SddModel: parse, Evaluate contract, and the Step-2 gate.
/// </summary>
public class SddModelTests
{
    // ── Direct construction helpers ───────────────────────────────────────────

    /// <summary>Build a simple 1-port linear-SDD: I[1,0] = _v1 / R.</summary>
    private static SddModel LinearSdd1Port(double r)
    {
        var expr = Parser.Parse($"_v1 / {r}");
        return new SddModel("R_sdd", 1,
            currentAst: [expr],
            chargeAst:  [null],
            parameters: new Dictionary<string, double>());
    }

    // ── Evaluate contract ─────────────────────────────────────────────────────

    [Fact]
    public void LinearSdd_Evaluate_CorrectCurrentAndConductance()
    {
        var sdd = LinearSdd1Port(50.0);
        var result = sdd.Evaluate(new PortVoltages([10.0]));

        Assert.Single(result.I);
        Assert.Equal(10.0 / 50.0, result.I[0], 10);   // i = v/R
        Assert.Equal(1.0  / 50.0, result.Dg[0, 0], 10); // dg = 1/R
    }

    [Fact]
    public void LinearSdd_ChargeIsZero()
    {
        var sdd = LinearSdd1Port(50.0);
        var result = sdd.Evaluate(new PortVoltages([5.0]));
        Assert.Equal(0.0, result.Q[0]);
        Assert.Equal(0.0, result.Dc[0, 0]);
    }

    [Fact]
    public void ModelKind_IsNonlinear()
    {
        var sdd = LinearSdd1Port(50.0);
        Assert.Equal(ModelKind.Nonlinear, sdd.Kind);
    }

    // ── CnlReader + Elaborator round-trip ────────────────────────────────────

    private static SddModel ParseSdd(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ec = nl.Components.First(c => c.Model is SddModel);
        return (SddModel)ec.Model;
    }

    [Fact]
    public void SimpleLinearSdd_ParsesAndEvaluates()
    {
        var sdd = ParseSdd(@"
R_val = 50
SDD:S1  n1 0  I[1,0]=_v1/R_val
");
        var r = sdd.Evaluate(new PortVoltages([10.0]));
        Assert.Equal(0.2, r.I[0], 10);      // i = 10/50 = 0.2 A
        Assert.Equal(0.02, r.Dg[0, 0], 10); // dg = 1/50
    }

    [Fact]
    public void TwoPort_ScopeVarsInjected_EvaluatesCorrectly()
    {
        // I[1,0] = _v1 / R1, I[2,0] = _v2 * G2
        var sdd = ParseSdd(@"
R1 = 100
G2 = 0.01
SDD:X1  gate 0 drain 0  I[1,0]=_v1/R1  I[2,0]=_v2*G2
");
        var r = sdd.Evaluate(new PortVoltages([5.0, 20.0]));
        Assert.Equal(5.0 / 100.0, r.I[0], 10);
        Assert.Equal(20.0 * 0.01, r.I[1], 10);
        // dg[0,0] = 1/R1, dg[1,1] = G2
        Assert.Equal(1.0 / 100.0, r.Dg[0, 0], 10);
        Assert.Equal(0.01,        r.Dg[1, 1], 10);
        // Cross terms should be zero for independent ports
        Assert.Equal(0.0, r.Dg[0, 1], 10);
        Assert.Equal(0.0, r.Dg[1, 0], 10);
    }

    // ── Hard-error cases ─────────────────────────────────────────────────────

    [Fact]
    public void ImplicitEquation_F_ThrowsHardError()
    {
        var ex = Assert.ThrowsAny<Exception>(() => ParseSdd(@"
SDD:X1  n1 0  F[1,0]=_v1
"));
        Assert.Contains("F[", ex.Message);
    }

    [Fact]
    public void ControlRef_CDeclaration_ParsesAndPopulatesControlRefs()
    {
        // C[n]=<instance> was previously a hard error; now it declares a control reference
        // that the DC engine resolves at run time. Verify the factory populates ControlRefs.
        var sdd = ParseSdd(@"
R:R1    n1 0  R=50 Ohm
SDD:X1  n1 0  I[1,0]=_v1  C[1]=R1
");
        Assert.Single(sdd.ControlRefs);
        Assert.Equal(1,    sdd.ControlRefs[0].N);
        Assert.Equal("R1", sdd.ControlRefs[0].RefInstance);
        Assert.Equal(0,    sdd.ControlRefs[0].Port);  // Cport absent → sentinel 0
    }

    [Fact]
    public void WeightingW2_MissingH_ThrowsCrossValidationError()
    {
        // I[1,2] without H[2] must error with a clear "not defined" message.
        var ex = Assert.ThrowsAny<Exception>(() => ParseSdd(@"
SDD:X1  n1 0  I[1,2]=_v1
"));
        Assert.Contains("H[2]", ex.Message);
        Assert.Contains("not defined", ex.Message);
    }

    // ── Charge equation plumbing ──────────────────────────────────────────────

    [Fact]
    public void ChargeEquation_I_p_1_Parses_AndReturnsNonZeroQ()
    {
        // A simple capacitor-like charge: q = C * _v1
        var sdd = ParseSdd(@"
Cv = 1e-12
SDD:X1  n1 0  I[1,0]=0  I[1,1]=Cv*_v1
");
        var r = sdd.Evaluate(new PortVoltages([5.0]));
        Assert.Equal(0.0,                r.I[0],  10);  // no current
        Assert.True(Math.Abs(r.Q[0] - 1e-12 * 5.0) < 1e-25, $"q={r.Q[0]:G}");
        Assert.True(Math.Abs(r.Dc[0,0] - 1e-12) < 1e-24, $"dc={r.Dc[0,0]:G}");
    }

    // ── STEP 2 GATE TEST ─────────────────────────────────────────────────────

    // Hero GaN HEMT i2 expression — must match nonlinear-dc §5.1 exactly.
    // Written without spaces so the .cnl tokenizer (whitespace-split) keeps it as one token.
    private const string HeroI2 =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*log(exp(-(Sv-_v1)/Sc)+1)))+1))" +
        "*log(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*log(exp(-(Sv-_v1)/Sc)+1))/TC)+1)" +
        "*(_v2*lam+1))/2";

    private const string HeroCnl = $@"
Sv  = -0.837
Sc  = 0.71
TV0 = 4.268
TC  = 1.507
th  = 0.001
a   = 0.176
g   = 0.089
lam = 0.0012
B   = 1130

SDD:M1  gate 0 drain 0  I[1,0]=_v1/50  I[2,0]={HeroI2}
";

    [Fact]
    public void HeroSdd_ParsesSuccessfully()
    {
        // Should not throw.
        var sdd = ParseSdd(HeroCnl);
        Assert.Equal(2, sdd.PortCount);
    }

    /// <summary>
    /// Step 2 gate: Evaluate at the bias returns golden currents and AD dg matches Step 1.
    /// </summary>
    [Fact]
    public void HeroSdd_Evaluate_ReturnsGoldenCurrentsAndDg()
    {
        var sdd = ParseSdd(HeroCnl);
        var result = sdd.Evaluate(new PortVoltages([-3.05, 48.0]));

        // i1 = vgs / 50 = -3.05/50 = -61.0 mA (exact linear)
        Assert.Equal(-3.05 / 50.0, result.I[0], 8);

        // i2 ≈ 49.11 mA (within 1 mA — value sanity; derivative is the gate criterion)
        Assert.True(Math.Abs(result.I[1] - 49.11e-3) < 1e-3,
            $"i2 = {result.I[1] * 1000:F3} mA, expected ≈ 49.11 mA");

        // dg[0,0] = di1/dv1 = 1/50 = 0.02 S
        Assert.Equal(1.0 / 50.0, result.Dg[0, 0], 8);
        // dg[0,1] = di1/dv2 = 0 (i1 = v1/50 has no v2 dependence)
        Assert.Equal(0.0, result.Dg[0, 1], 8);

        // gm = dg[1,0] = ∂i2/∂v1 ≈ 62.4 mS (within 1%)
        double gm  = result.Dg[1, 0];
        double gds = result.Dg[1, 1];

        Assert.True(Math.Abs(gm - 62.4e-3) / 62.4e-3 < 0.01,
            $"gm = {gm * 1000:F4} mS, expected ≈ 62.4 mS");

        // gds = dg[1,1] = ∂i2/∂v2 ≈ -9.45 µS (negative! §5.3)
        Assert.True(gds < 0.0,
            $"gds must be negative (§5.3), got {gds * 1e6:F4} µS");
        Assert.True(Math.Abs(gds - (-9.45e-6)) / 9.45e-6 < 0.01,
            $"gds = {gds * 1e6:F4} µS, expected ≈ -9.45 µS");
    }
}
