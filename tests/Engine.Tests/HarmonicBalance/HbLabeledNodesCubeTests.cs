using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for brief node-picker-labeled-filter (Engine hops):
///
/// T4 — Cube_Has_LabeledNodes_SideCube
/// T6 — HandWritten_NoLabeledNodes
/// </summary>
public class HbLabeledNodesCubeTests
{
    // Simple SDD amplifier CNL (no schematic labels; used for T4 with manual LabeledNets injection
    // and for T6 which verifies the cube is absent when LabeledNets is empty).
    private const string SimpleCnl = @"
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

R:Rload   n_drain  0  R=50

analysis HB1  type=hb  Tone=2e9  MaxHarm=3  Tol=1e-4
";

    private static DataSet RunHbWithLabels(string cnl, params string[] labeledNets)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        // Simulate what NetExtractor would populate from schematic labels.
        foreach (var n in labeledNets)
            tb.LabeledNets.Add(n);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var hba     = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        return (DataSet)new HbEngine(netlist, tb).Run(p);
    }

    // ── T4: __LabeledNodes side cube appears for labeled runs ─────────────────

    /// <summary>
    /// When TestBench.LabeledNets is non-empty, HbEngine must emit a __LabeledNodes
    /// side cube whose axis Labels contain exactly the labeled names that appear in
    /// the V cube's node axis.
    /// </summary>
    [Fact]
    public void T4_Cube_Has_LabeledNodes_SideCube()
    {
        var ds = RunHbWithLabels(SimpleCnl, "n_drain", "n_gate");

        Assert.True(ds.Contains("__LabeledNodes"),
            "__LabeledNodes cube must be present when LabeledNets is non-empty");

        var lblCube = ds["__LabeledNodes"];
        Assert.True(lblCube.Axes.Count > 0, "__LabeledNodes must have at least one axis");
        var labels = lblCube.Axes[0].Labels;
        Assert.NotNull(labels);

        // Both labeled names must appear in the side cube.
        Assert.Contains("n_drain", labels!);
        Assert.Contains("n_gate",  labels!);

        // Auto-named internal nodes (n_gbias, n_dbias, etc.) must NOT appear.
        Assert.DoesNotContain("n_gbias", labels!);
        Assert.DoesNotContain("n_dbias", labels!);

        // Verify the V cube itself contains those nodes.
        var vCube     = ds["V"];
        string[] vLabels = vCube.Axes[0].Labels!;
        Assert.Contains("n_drain", vLabels);
        Assert.Contains("n_gate",  vLabels);
    }

    // ── T6: hand-written netlist produces no __LabeledNodes cube ─────────────

    /// <summary>
    /// A CNL loaded directly (no schematic → empty LabeledNets) must not produce
    /// a __LabeledNodes cube. "No provenance info" = show-all default (handled by the UI).
    /// </summary>
    [Fact]
    public void T6_HandWritten_NoLabeledNodes()
    {
        // No LabeledNets injected — simulates pure CNL workflow.
        var ds = RunHbWithLabels(SimpleCnl);

        Assert.False(ds.Contains("__LabeledNodes"),
            "__LabeledNodes must be absent when no labeled nets are present (hand-written CNL)");
    }
}
