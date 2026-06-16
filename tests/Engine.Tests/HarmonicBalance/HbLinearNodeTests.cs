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
/// Gate tests for brief hb-linear-nodes-in-cube:
/// the HB V cube's node axis now includes all non-ground user nodes, not only the
/// nonlinear-device interface nodes.  Linear-only nodes are recovered via
/// HbLinearBackSolver; __-prefixed internal mint nodes are excluded.
///
/// T1 — Hb_VCube_IncludesLinearNode
/// T2 — Hb_LinearNode_INlZero
/// T3 — Hb_InterfaceNodes_Unchanged
/// T4 — Hb_InternalNodesFiltered
/// T5 — Sweep_FullNodes
/// </summary>
public class HbLinearNodeTests(ITestOutputHelper output)
{
    // ── Shared CNL: square-law FET with RC voltage divider off drain ──────────
    // Vout2 is connected only to R:Rdiv1 and R:Rdiv2 → linear-only node.
    // At every harmonic k: Vout2[k] = V[n_drain,k] * 40/(10+40) = 0.8 * V[n_drain,k].
    private const string LinearNodeCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs   n_gbias 0  Vdc=Vgg  Freq=2e9  V=0.1  Phase=0
L:Lbias_g    n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d    n_dbias n_drain  L=1  R=0

R:Rload   n_drain  0      R=50
R:Rdiv1   n_drain  Vout2  R=10
R:Rdiv2   Vout2    0      R=40

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-6
";

    // Same circuit swept over 2 drive levels.
    private const string SweepCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0
Vs_mag = 0.1

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs   n_gbias 0  Vdc=Vgg  Freq=2e9  V=Vs_mag  Phase=0
L:Lbias_g    n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d    n_dbias n_drain  L=1  R=0

R:Rload   n_drain  0      R=50
R:Rdiv1   n_drain  Vout2  R=10
R:Rdiv2   Vout2    0      R=40

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
analysis SW1  type=parametric_sweep  Var=Vs_mag  Values=0.1,0.2  Inner=HB1
";

    // Circuit using P1Tone: mints __p1tone_Vs_drv internal node.
    private const string P1ToneCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

P1Tone:Vs    n_gate 0  Pavl=-10  Freq=2e9
V:Vgg        n_gbias 0  V=Vgg
L:Lbias_g    n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d    n_dbias n_drain  L=1  R=0

R:Rload   n_drain 0  R=50

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (DataSet ds, HbRunResult result) RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var result    = new HbEngine(netlist, tb).Run(p);
        return ((DataSet)result, result);
    }

    private static DataSet RunSweep(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var psa       = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        return ParametricSweepEngine.Run(psa, lib, tb);
    }

    private static int NodeIndex(DataCube cube, string name)
        => Array.FindIndex(cube.Axes[0].Labels!, n =>
            n.Equals(name, StringComparison.Ordinal));

    // ── T1: linear-only node appears in V cube ────────────────────────────────

    /// <summary>
    /// Vout2 is connected only to R:Rdiv1 and R:Rdiv2 — no nonlinear device port.
    /// After Run(), ds["V"].Axes[0].Labels must contain "Vout2".
    /// Its fundamental voltage must equal 0.8 × V[n_drain, 1] (resistive divider).
    /// </summary>
    [Fact]
    public void T1_Hb_VCube_IncludesLinearNode()
    {
        var (ds, result) = RunHb(LinearNodeCnl);

        var vCube  = ds["V"];
        string[] labels = vCube.Axes[0].Labels!;

        output.WriteLine($"V cube node labels: [{string.Join(", ", labels)}]");

        int vout2Idx = NodeIndex(vCube, "Vout2");
        Assert.True(vout2Idx >= 0, "Vout2 must appear in the node axis labels");

        int drainIdx = NodeIndex(vCube, "n_drain");
        Assert.True(drainIdx >= 0, "n_drain must appear in the node axis labels");

        // At the fundamental (k=1): Vout2 = 0.8 × V[n_drain,1] (40/(10+40) divider).
        Complex vDrainFund  = (Complex)vCube[drainIdx, 1];
        Complex vOut2Fund   = (Complex)vCube[vout2Idx, 1];
        Complex expected    = 0.8 * vDrainFund;

        double relErr = vDrainFund.Magnitude > 1e-9
            ? (vOut2Fund - expected).Magnitude / vDrainFund.Magnitude
            : (vOut2Fund - expected).Magnitude;

        output.WriteLine(
            $"V[n_drain,1]={vDrainFund:G4}  V[Vout2,1]={vOut2Fund:G4}  " +
            $"expected={expected:G4}  relErr={relErr:E3}");

        Assert.True(relErr < 1e-5,
            $"Vout2 fundamental should equal 0.8×V[n_drain,1] (relErr={relErr:E3})");

        // Cross-check at DC (k=0) too: same divider holds at every harmonic.
        Complex vDrainDc = (Complex)vCube[drainIdx, 0];
        Complex vOut2Dc  = (Complex)vCube[vout2Idx, 0];
        double  dcRelErr = vDrainDc.Magnitude > 1e-9
            ? (vOut2Dc - 0.8 * vDrainDc).Magnitude / vDrainDc.Magnitude
            : (vOut2Dc - 0.8 * vDrainDc).Magnitude;
        output.WriteLine($"DC cross-check: relErr={dcRelErr:E3}");
        Assert.True(dcRelErr < 1e-5, $"Vout2 DC should equal 0.8×V[n_drain,0] (relErr={dcRelErr:E3})");

        output.WriteLine("T1 PASS — Vout2 in node axis, voltage matches resistive divider.");
    }

    // ── T2: INl is zero at the linear node ───────────────────────────────────

    /// <summary>
    /// No nonlinear device is connected to Vout2, so INl[Vout2, k] must be 0 for all k.
    /// </summary>
    [Fact]
    public void T2_Hb_LinearNode_INlZero()
    {
        var (ds, _) = RunHb(LinearNodeCnl);

        var inlCube = ds["INl"];
        string[] labels = inlCube.Axes[0].Labels!;

        int vout2Idx = Array.FindIndex(labels, n => n.Equals("Vout2", StringComparison.Ordinal));
        Assert.True(vout2Idx >= 0, "Vout2 must appear in INl node axis");

        int K1 = inlCube.Axes[1].Length;
        for (int k = 0; k < K1; k++)
        {
            Complex inl = (Complex)inlCube[vout2Idx, k];
            Assert.Equal(Complex.Zero, inl);
        }

        output.WriteLine($"T2 PASS — INl[Vout2, k] == 0 for all {K1} harmonics.");
    }

    // ── T3: interface-node voltages are unchanged ─────────────────────────────

    /// <summary>
    /// The V cube values at n_gate and n_drain must equal the back-solver's reconstruction
    /// (they are sourced from the converged Newton V, which the back-solver also recovers
    /// within Newton tolerance).
    /// </summary>
    [Fact]
    public void T3_Hb_InterfaceNodes_Unchanged()
    {
        var (ds, result) = RunHb(LinearNodeCnl);

        var bs = result.BackSolver;
        Assert.NotNull(bs);

        var vCube = ds["V"];
        string[] labels = vCube.Axes[0].Labels!;
        int K1 = vCube.Axes[1].Length;

        // Tolerance: loose relative tolerance bounded by Newton convergence.
        const double RelTol = 1e-5;

        foreach (string ifName in new[] { "n_gate", "n_drain" })
        {
            int ni = Array.FindIndex(labels, n => n.Equals(ifName, StringComparison.Ordinal));
            Assert.True(ni >= 0, $"Interface node '{ifName}' must be in V cube");

            Assert.True(bs.TryGetNodeNumber(ifName, out int circNode),
                $"Back-solver must recognize '{ifName}'");

            for (int k = 0; k < K1; k++)
            {
                Complex vCubeVal = (Complex)vCube[ni, k];
                Complex vBack    = bs.GetNodeVoltage(circNode, k, 0);
                double  absErr   = (vCubeVal - vBack).Magnitude;
                double  thresh   = RelTol * vCubeVal.Magnitude + 1e-9;

                Assert.True(absErr <= thresh,
                    $"{ifName} k={k}: cube={vCubeVal:G6} back={vBack:G6} absErr={absErr:E3}");
            }

            output.WriteLine($"T3: {ifName} matches back-solver at all harmonics. ✓");
        }

        output.WriteLine("T3 PASS — interface-node voltages in full cube match converged solution.");
    }

    // ── T4: __-prefixed internal mint nodes are filtered out ─────────────────

    /// <summary>
    /// P1Tone mints __p1tone_Vs_drv.  After Run(), no label in ds["V"].Axes[0].Labels
    /// starts with "__".
    /// </summary>
    [Fact]
    public void T4_Hb_InternalNodesFiltered()
    {
        var (ds, _) = RunHb(P1ToneCnl);

        string[] labels = ds["V"].Axes[0].Labels!;
        output.WriteLine($"V cube node labels: [{string.Join(", ", labels)}]");

        var mintNodes = labels.Where(n => n.StartsWith("__", StringComparison.Ordinal)).ToArray();
        if (mintNodes.Length > 0)
            output.WriteLine($"UNEXPECTED mint nodes: [{string.Join(", ", mintNodes)}]");

        Assert.True(mintNodes.Length == 0,
            $"__-prefixed internal mint nodes must not appear in the V cube. Found: [{string.Join(", ", mintNodes)}]");

        // n_gate and n_drain should still be present.
        Assert.Contains("n_gate",  (IEnumerable<string>)labels);
        Assert.Contains("n_drain", (IEnumerable<string>)labels);

        output.WriteLine("T4 PASS — no __ mint nodes in V cube; interface nodes present.");
    }

    // ── T5: ParametricSweep stacks the full node axis at every sweep point ────

    /// <summary>
    /// After ParametricSweepEngine stacks two sweep points, the V cube has axes
    /// [Vs_mag, node, harmonic].  Vout2 is in the node axis; at each sweep point
    /// the resistive-divider ratio V[Vout2] = 0.8 × V[n_drain] holds at all harmonics.
    /// </summary>
    [Fact]
    public void T5_Sweep_FullNodes()
    {
        var ds    = RunSweep(SweepCnl);
        var vCube = ds["V"];

        output.WriteLine($"V cube rank={vCube.Rank}  axes=[{string.Join(", ", vCube.Axes.Select(a => $"{a.Name}({a.Length})"))}]");

        // Expect axes [Vs_mag(2), node(?), harmonic(4)].
        Assert.Equal(3, vCube.Rank);
        Assert.Equal("Vs_mag",   vCube.Axes[0].Name);
        Assert.Equal("node",     vCube.Axes[1].Name);
        Assert.Equal("harmonic", vCube.Axes[2].Name);

        string[] labels = vCube.Axes[1].Labels!;
        int vout2Idx = Array.FindIndex(labels, n => n.Equals("Vout2", StringComparison.Ordinal));
        int drainIdx = Array.FindIndex(labels, n => n.Equals("n_drain", StringComparison.Ordinal));

        Assert.True(vout2Idx >= 0, "Vout2 must appear in node axis after stacking");
        Assert.True(drainIdx >= 0, "n_drain must appear in node axis after stacking");

        int nSweep = vCube.Axes[0].Length;  // 2 sweep points
        int K1     = vCube.Axes[2].Length;

        output.WriteLine($"Checking divider ratio 0.8 × V[n_drain] = V[Vout2] at {nSweep} sweep pts × {K1} harmonics");

        for (int si = 0; si < nSweep; si++)
        for (int k  = 0; k  < K1;     k++)
        {
            Complex vDrain = (Complex)vCube[si, drainIdx, k];
            Complex vOut2  = (Complex)vCube[si, vout2Idx, k];
            Complex exp    = 0.8 * vDrain;
            double  relErr = vDrain.Magnitude > 1e-9
                ? (vOut2 - exp).Magnitude / vDrain.Magnitude
                : (vOut2 - exp).Magnitude;
            Assert.True(relErr < 1e-4,
                $"si={si} k={k}: V[Vout2]={vOut2:G4} expected 0.8×V[n_drain]={exp:G4} relErr={relErr:E3}");
        }

        output.WriteLine("T5 PASS — swept cube includes Vout2 at all points with correct divider ratio.");
    }
}
