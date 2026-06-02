using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// Tests for the nonlinear-DC Newton solver, including the Step-3 hero gate.
/// </summary>
public class NonlinearDcTests
{
    private static NonlinearDcEngine.DcResult Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return NonlinearDcEngine.Run(nl);
    }

    // ── Sanity: purely linear circuit (SDD as a resistor) ────────────────────

    [Fact]
    public void LinearSddResistor_ConvergesToCorrectVoltage()
    {
        // Circuit: 1V source through R=50Ω (as SDD), shunt R=50Ω to ground.
        // V_mid = 1V * 50/(50+50) = 0.5V
        var result = Run(@"
R_val = 50
SDD:R1  n1 0  I[1,0]=_v1/R_val
R:R2    n1 0  R=50 Ohm
V:VS    n1 0  V=1
");
        Assert.True(result.Converged, $"Did not converge. Residual={result.FinalResidual:G}");
        // n1 = circuit node 1 (first non-ground node). With a 1V voltage source,
        // n1 should be 1V (VS forces it).
        Assert.True(Math.Abs(result.NodeVoltages[0] - 1.0) < 1e-4,
            $"V(n1)={result.NodeVoltages[0]:G}, expected ≈ 1 V");
    }

    // ── Sanity: two-node circuit with purely linear components ────────────────

    [Fact]
    public void PureResistorDivider_MatchesAnalytic()
    {
        // Vdd=10V → R1=30Ω → n1 → R2=20Ω → gnd
        // V(n1) = 10 * 20/(30+20) = 4.0 V
        var result = Run(@"
R:R1  vdd n1  R=30 Ohm
R:R2  n1  0   R=20 Ohm
V:VS  vdd 0   V=10
");
        Assert.True(result.Converged, $"Did not converge. Residual={result.FinalResidual:G}");
        // Find the node index for n1. The nodes are in elaboration order.
        // Typical order: vdd=node1, n1=node2 (or whatever the elaborator assigns).
        // We check that the voltage divider equation holds: V(vdd) ≈ 10, V(n1) ≈ 4
        double[] v = result.NodeVoltages;
        // node 0-based index — just check both sum to 10V and n1≈4V
        // The elaborator assigns nodes in encounter order.
        // With "vdd" first, v[0]=V(vdd)≈10, v[1]=V(n1)≈4 (assuming no choke)
        Assert.True(v.Length >= 2, "Expected at least 2 voltage unknowns");
        double vdd_v = v.Max();  // 10V node
        double n1_v  = v.Min(x => Math.Abs(x - 4.0)) < 0.01 ? v.First(x => Math.Abs(x - 4.0) < 0.01) : double.NaN;
        Assert.True(!double.IsNaN(n1_v), $"Expected a node at ≈4V, got: [{string.Join(", ", v.Select(x => x.ToString("F3")))}]");
    }

    // ── STEP 3 HERO GATE ─────────────────────────────────────────────────────

    // Hero GaN HEMT i2 expression (§5.1), no spaces for .cnl tokenizer.
    private const string HeroI2 =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*log(exp(-(Sv-_v1)/Sc)+1)))+1))" +
        "*log(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*log(exp(-(Sv-_v1)/Sc)+1))/TC)+1)" +
        "*(_v2*lam+1))/2";

    // Hero bias circuit (§5.2):
    //   Gate: V_gate = −3.05V source, series "choke" = short (R≈0, or just a 1Ω for Phase 3)
    //   Drain: V_dd = +48V source through Rd = 20Ω
    //   SDD: grounded-source 2-port (gate, drain, source=gnd)
    //
    // At convergence: vgs = −3.05V (forced by gate source + choke short),
    //   vds = 48 − i2*20 ≈ 47.018V, i2 ≈ 49.12mA.
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

; Gate: −3.05V source → small series R (choke at DC = short, use 1e-6 Ohm) → gate node
V:Vg  vgs 0  V=-3.05
R:Rg  vgs gate  R=1e-6 Ohm

; Drain: +48V source → Rd=20Ω → drain node
V:Vd  vdd 0  V=48
R:Rd  vdd drain  R=20 Ohm

; GaN HEMT SDD (source=ground)
SDD:M1  gate 0 drain 0  I[1,0]=_v1/50  I[2,0]={HeroI2}
";

    private const string Hero2Cnl = $@"
Sv  = -0.837
Sc  = 0.71
TV0 = 4.268
TC  = 1.507
th  = 0.001
a   = 0.176
g   = 0.089
lam = 0.0012
B   = 1130

; Gate: −3.05V source → small series R (choke at DC = short, use 1e-6 Ohm) → gate node
V:Vg  vgs 0  V=-3.05
R:Rg  vgs gate  R=1e-6 Ohm

; Drain: +48V source → Rd=20Ω → drain node
V:Vd  vdd 0  V=48
R:Rd  vdd drain  R=20 Ohm

; GaN HEMT SDD (source=ground)
SDD:M1  gate 0 drain 0  I[1,0]=_v1/50  I[2,0]={HeroI2}


; Gate: −3.05V source → small series R (choke at DC = short, use 1e-6 Ohm) → gate node
V:Vg2  vgs2 0  V=-3.05
R:Rg2  vgs2 gate2  R=1e-6 Ohm

; Drain: +48V source → Rd=40Ω → drain node
V:Vd2  vdd2 0  V=48
R:Rd2  vdd2 drain2  R=40 Ohm

; GaN HEMT SDD (source=ground)
SDD:M2  gate2 0 drain2 0  I[1,0]=_v1/50  I[2,0]={HeroI2}


";


    [Fact]
    public void HeroFet_ConvergesWithSeriesRd()
    {
        var result = Run(HeroCnl);

        Assert.True(result.Converged,
            $"Hero did not converge after {result.Iterations} iterations. " +
            $"Residual={result.FinalResidual:G3}");

        // Identify nodes by value.
        // Expected: vgs = −3.05V (gate node), vds ≈ 47.018V (drain node), vdd ≈ 48V, vgs_src≈−3.05V
        double[] v = result.NodeVoltages;

        // Find the drain node: should be ≈ 47.018V
        double vds = v.OrderBy(x => Math.Abs(x - 47.018)).First();
        Assert.True(Math.Abs(vds - 47.018) < 0.05,
            $"vds ≈ {vds:F3} V, expected ≈ 47.018 V. All node voltages: [{string.Join(", ", v.Select(x => $"{x:F4}"))}]");

        // Find the gate node: should be ≈ −3.05V
        double vgs = v.OrderBy(x => Math.Abs(x - (-3.05))).First();
        Assert.True(Math.Abs(vgs - (-3.05)) < 0.01,
            $"vgs ≈ {vgs:F4} V, expected ≈ −3.05 V");

        // Drain current in series Rd: i2 = (48 − vds) / 20
        double i2_in_series_Rd = (48.0 - vds) / 20.0;
        Assert.True(Math.Abs(i2_in_series_Rd - 49.12e-3) < 0.01e-3,
            $"i2 ≈ {i2_in_series_Rd * 1000:F2} mA, expected ≈ 49.12 mA");
        

    }

    [Fact]
    public void HeroFet_TwoFETsConvergeWithDifferentSeriesRd()
    {
        var result = Run(Hero2Cnl);

        Assert.True(result.Converged,
            $"Hero did not converge after {result.Iterations} iterations. " +
            $"Residual={result.FinalResidual:G3}");

        // Identify nodes by value.
        // Expected: vgs = −3.05V (gate node), vds ≈ 47.018V (drain node), vdd ≈ 48V, vgs_src≈−3.05V
        double[] v = result.NodeVoltages;

        // Find the drain node: should be ≈ 47.018V
        double vds = v.OrderBy(x => Math.Abs(x - 47.018)).First();
        Assert.True(Math.Abs(vds - 47.018) < 0.05,
            $"M1 vds ≈ {vds:F3} V, expected ≈ 47.018 V. All node voltages: [{string.Join(", ", v.Select(x => $"{x:F4}"))}]");

        // Find the drain node for M2: should be ≈ 46.035V
        double vds2 = v.OrderBy(x => Math.Abs(x - 46.035)).First();
        Assert.True(Math.Abs(vds2 - 46.035) < 0.05,
            $"M2 vds2 ≈ {vds2:F3} V, expected ≈ 46.035 V. All node voltages: [{string.Join(", ", v.Select(x => $"{x:F4}"))}]");


        // Find the gate node: should be ≈ −3.05V
        double vgs = v.OrderBy(x => Math.Abs(x - (-3.05))).First();
        Assert.True(Math.Abs(vgs - (-3.05)) < 0.01,
            $"vgs ≈ {vgs:F4} V, expected ≈ −3.05 V");

        // Drain current in series Rd for M1: i2 = (48 − vds) / 20
        double i2_in_series_Rd = (48.0 - vds) / 20.0;
        Assert.True(Math.Abs(i2_in_series_Rd - 49.12e-3) < 0.01e-3,
            $"i2 ≈ {i2_in_series_Rd * 1000:F2} mA, expected ≈ 49.12 mA");

        // Drain current in series Rd for M2: i2 = (48 − vds2) / 40
        double i2_in_series_Rd_M2 = (48.0 - vds2) / 40.0;
        Assert.True(Math.Abs(i2_in_series_Rd_M2 - 49.13e-3) < 0.01e-3,
            $"i2 ≈ {i2_in_series_Rd_M2 * 1000:F2} mA, expected ≈ 49.13 mA");// slightly different current

    }



    // ── Robustness: overshooting iterate should not NaN ───────────────────────

    [Fact]
    public void HeroFet_Robustness_NoNaNUnderOvershoot()
    {
        // Run the hero — the source-stepping continuation deliberately creates
        // intermediate iterates that probe extreme v1/v2 values.
        // This test checks that no NaN propagates through to the result.
        var result = Run(HeroCnl);
        Assert.True(double.IsFinite(result.FinalResidual), "Final residual must be finite");
        foreach (var v in result.NodeVoltages)
            Assert.True(double.IsFinite(v), $"Node voltage {v} is not finite");
    }
}
