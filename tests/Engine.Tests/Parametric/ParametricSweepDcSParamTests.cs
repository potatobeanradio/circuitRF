using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// Sweep Fix 2 gate: ParametricSweepEngine.RunInner now dispatches SParameterAnalysis and
/// DcAnalysis in addition to HarmonicBalanceAnalysis and ParametricSweepAnalysis.
/// </summary>
public class ParametricSweepDcSParamTests(ITestOutputHelper output)
{
    // ── 1. S-param sweep over a resistor value ────────────────────────────────

    private const string SParamSweepCnl = @"
Rval = 50

Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=Rval Ohm

analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
analysis SW1  type=parametric_sweep  Var=Rval  Values=25,50,100  Inner=SP1
";

    [Fact]
    public void Sweep_SParam_OverVariable()
    {
        var (lib, tb) = new CnlReader().Read(SParamSweepCnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);

        var sCube = ds["S"];
        output.WriteLine($"S cube rank={sCube.Rank}  axes=[{string.Join(", ", sCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // S cube axes: [Rval(3), freq(1), port_i(2), port_j(2)]
        Assert.Equal(4, sCube.Rank);
        Assert.Equal("Rval", sCube.Axes[0].Name);
        Assert.Equal(3, sCube.Axes[0].Length);
        Assert.Equal(25.0,  sCube.Axes[0].Values[0], 12);
        Assert.Equal(50.0,  sCube.Axes[0].Values[1], 12);
        Assert.Equal(100.0, sCube.Axes[0].Values[2], 12);

        // Run a direct S-param at Rval=50 and confirm the Rval=50 slice matches.
        const string directCnl50 = @"
Rval = 50
Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=Rval Ohm
";
        var (lib50, tb50) = new CnlReader().Read(directCnl50);
        var nl50 = new Elaborator(lib50).Elaborate(tb50);
        var direct50 = SParameterEngine.Run(nl50, [1e9]);

        // S[Rval=50, freq[0], 0, 0] = S11 from sweep
        // S[freq[0], 0, 0] = S11 from direct run
        // Both should agree to within 1e-9.
        const double Tol = 1e-9;
        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
        {
            Complex sweptS  = (Complex)sCube[1, 0, r, c];  // Rval=50 is index 1
            Complex directS = (Complex)direct50["S"][0, r, c];
            double  err     = (sweptS - directS).Magnitude;
            output.WriteLine($"S[{r+1},{c+1}]: swept={sweptS:G4}  direct={directS:G4}  err={err:G3}");
            Assert.True(err < Tol,
                $"S[{r+1},{c+1}] mismatch at Rval=50: swept={sweptS:G4} direct={directS:G4} err={err:G3}");
        }

        output.WriteLine("Sweep_SParam_OverVariable: PASS.");
    }

    // ── 2. DC sweep over a bias variable ─────────────────────────────────────

    private const string DcSweepCnl = @"
Vbias = 5

V:Vs  n1 0  V=Vbias
R:R1  n1 0  R=100 Ohm

analysis DC1  type=dc
analysis SW1  type=parametric_sweep  Var=Vbias  Values=5,10  Inner=DC1
";

    [Fact]
    public void Sweep_Dc_OverVariable()
    {
        var (lib, tb) = new CnlReader().Read(DcSweepCnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // V cube axes: [Vbias(2), node(1)]
        Assert.Equal(2, vCube.Rank);
        Assert.Equal("Vbias", vCube.Axes[0].Name);
        Assert.Equal(2, vCube.Axes[0].Length);
        Assert.Equal("node", vCube.Axes[1].Name);
        Assert.Equal(1, vCube.Axes[1].Length);

        // At Vbias=5 (index 0): node n1 should be ≈ 5 V (voltage source pins it).
        double v5 = (double)vCube[0, 0];
        output.WriteLine($"V(n1) at Vbias=5:  {v5:F4} V");
        Assert.True(Math.Abs(v5 - 5.0) < 1e-6, $"Expected ≈5 V, got {v5:G}");

        // At Vbias=10 (index 1): node n1 should be ≈ 10 V.
        double v10 = (double)vCube[1, 0];
        output.WriteLine($"V(n1) at Vbias=10: {v10:F4} V");
        Assert.True(Math.Abs(v10 - 10.0) < 1e-6, $"Expected ≈10 V, got {v10:G}");

        // Cross-check against a direct DC run at Vbias=5.
        const string directCnl5 = @"
Vbias = 5
V:Vs  n1 0  V=Vbias
R:R1  n1 0  R=100 Ohm
";
        var (lib5, tb5) = new CnlReader().Read(directCnl5);
        var nl5 = new Elaborator(lib5).Elaborate(tb5);
        var direct5 = NonlinearDcEngine.Run(nl5);
        Assert.True(direct5.Converged, "Direct DC run at Vbias=5 should converge.");
        Assert.True(Math.Abs(v5 - direct5.NodeVoltages[0]) < 1e-6,
            $"Sweep V at Vbias=5 ({v5:G}) should match direct DC ({direct5.NodeVoltages[0]:G}).");

        output.WriteLine("Sweep_Dc_OverVariable: PASS.");
    }

    // ── 3. Nested sweep: Vgs outer × Vds inner (DC curve tracer) ─────────────

    private const string CurveTracerCnl = @"
Vgs = -3.0
Vds = 5.0

V:Vgate   n_gate  0  V=Vgs
V:Vdrain  n_drain 0  V=Vds

analysis DC1     type=dc
analysis SW_Vds  type=parametric_sweep  Var=Vds  Values=0,5,10  Inner=DC1
analysis SW_Vgs  type=parametric_sweep  Var=Vgs  Values=-3,-3.5  Inner=SW_Vds
";

    [Fact]
    public void Sweep_Nested_DcCurveTracer()
    {
        var (lib, tb) = new CnlReader().Read(CurveTracerCnl);
        var swVgs = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW_Vgs");

        var ds = ParametricSweepEngine.Run(swVgs, lib, tb);

        var vCube = ds["V"];
        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // V cube axes after 2-level nesting: [Vgs(2), Vds(3), node(2)]
        Assert.Equal(3, vCube.Rank);
        Assert.Equal("Vgs", vCube.Axes[0].Name);
        Assert.Equal(2, vCube.Axes[0].Length);
        Assert.Equal("Vds", vCube.Axes[1].Name);
        Assert.Equal(3, vCube.Axes[1].Length);
        Assert.Equal("node", vCube.Axes[2].Name);
        Assert.Equal(2, vCube.Axes[2].Length);

        // Axis values correct.
        Assert.Equal(-3.0,  vCube.Axes[0].Values[0], 12);
        Assert.Equal(-3.5,  vCube.Axes[0].Values[1], 12);
        Assert.Equal(0.0,   vCube.Axes[1].Values[0], 12);
        Assert.Equal(5.0,   vCube.Axes[1].Values[1], 12);
        Assert.Equal(10.0,  vCube.Axes[1].Values[2], 12);

        // Spot-check: V at (Vgs=-3 [idx 0], Vds=5 [idx 1], n_drain [idx 1]) ≈ 5 V.
        // The voltage source V:Vdrain pins n_drain to Vds=5.
        double vDrainAt5 = (double)vCube[0, 1, 1];  // Vgs=-3, Vds=5, n_drain
        output.WriteLine($"V(n_drain) at (Vgs=-3, Vds=5): {vDrainAt5:F4} V");
        Assert.True(Math.Abs(vDrainAt5 - 5.0) < 1e-6,
            $"Expected V(n_drain)≈5 V at (Vgs=-3,Vds=5), got {vDrainAt5:G}");

        // Spot-check: V at (Vgs=-3.5 [idx 1], Vds=10 [idx 2], n_gate [idx 0]) ≈ -3.5 V.
        double vGateAt35 = (double)vCube[1, 2, 0];  // Vgs=-3.5, Vds=10, n_gate
        output.WriteLine($"V(n_gate) at (Vgs=-3.5, Vds=10): {vGateAt35:F4} V");
        Assert.True(Math.Abs(vGateAt35 - (-3.5)) < 1e-6,
            $"Expected V(n_gate)≈-3.5 V at (Vgs=-3.5,Vds=10), got {vGateAt35:G}");

        output.WriteLine("Sweep_Nested_DcCurveTracer: PASS.");
    }

    // ── 4. Unsupported inner type still throws ────────────────────────────────

    [Fact]
    public void Unsupported_StillThrows()
    {
        // A LoadpullAnalysis as the inner wraps no dispatch in ParametricSweepEngine.
        var tb = new TestBench("unsupported-test");
        tb.GlobalVariables.Add(new Variable("Rval", "50"));
        tb.Analyses.Add(new LoadpullAnalysis("LP1"));
        var psa = new ParametricSweepAnalysis("SW1", "Rval", [50.0], "LP1");
        tb.Analyses.Add(psa);
        var lib = new Library("test");

        var ex = Assert.Throws<NotSupportedException>(
            () => ParametricSweepEngine.Run(psa, lib, tb));

        Assert.Contains("LoadpullAnalysis", ex.Message);
        output.WriteLine($"Throws NotSupportedException: {ex.Message}");
        output.WriteLine("Unsupported_StillThrows: PASS.");
    }
}
