using System;
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

    // ── Stack_PreservesLabeledNodesShape ──────────────────────────────────────

    /// <summary>
    /// After <see cref="DataSet.StackSweepAxis"/>, the <c>__LabeledNodes</c> metadata
    /// cube must remain rank-1 (not rank-2).  Its axis must be named "label" and its
    /// Labels must still contain the original node names.
    /// Regression: before fix, PrependAxis was called on __LabeledNodes, making it rank-2
    /// with the sweep axis at position 0; Axes[0].Labels was then null → filter broke.
    /// </summary>
    [Fact]
    public void Stack_PreservesLabeledNodesShape()
    {
        // Build two per-sweep-point DataSets that each carry __LabeledNodes.
        static DataSet MakePoint(double sweepVal)
        {
            var nodeVals  = new[] { 1.0, 2.0 };
            var harmVals  = new[] { 0.0, 2e9 };
            var nodeAxis  = new Axis("node",     nodeVals, "", new[] { "n_drain", "n_gate" });
            var harmAxis  = new Axis("harmonic", harmVals, "Hz");
            var vData     = new System.Numerics.Complex[nodeVals.Length * harmVals.Length];

            var ds = new DataSet();
            ds.Add("V", new DataCube(new[] { nodeAxis, harmAxis }, vData));

            var lblIdx  = new[] { 0.0, 1.0 };
            var lblAxis = new Axis("label", lblIdx, "", new[] { "n_drain", "n_gate" });
            ds.Add("__LabeledNodes", new DataCube(new[] { lblAxis }, new double[2]));
            return ds;
        }

        var sweepAxis = new Axis("Pin", new[] { -10.0, 0.0 });
        var stacked   = DataSet.StackSweepAxis(sweepAxis, new[] { MakePoint(-10), MakePoint(0) });

        Assert.True(stacked.Contains("__LabeledNodes"), "__LabeledNodes must survive stacking");

        var lblCube = stacked["__LabeledNodes"];
        Assert.Equal(1, lblCube.Rank);
        Assert.Equal("label", lblCube.Axes[0].Name);
        Assert.NotNull(lblCube.Axes[0].Labels);
        Assert.Contains("n_drain", lblCube.Axes[0].Labels!);
        Assert.Contains("n_gate",  lblCube.Axes[0].Labels!);

        // V cube must still be rank-2 (sweep prepended makes it rank-3 with the original 2).
        // Wait: before the stack, V is rank-2 [node, harmonic].
        // After StackSweepAxis, V must be rank-3 [sweep, node, harmonic].
        var vCube = stacked["V"];
        Assert.Equal(3, vCube.Rank);
        Assert.Equal("Pin", vCube.Axes[0].Name);
    }

    // ── T7: CnlWriter/CnlReader round-trip emits __LabeledNodes ──────────────

    /// <summary>
    /// Regression guard for brief-cnl-labelednets-provenance: LabeledNets populated
    /// in-memory (as NetExtractor does from a schematic) must survive the
    /// CnlWriter → file → CnlReader path and still produce __LabeledNodes in HB output.
    ///
    /// Before the fix CnlWriter never emitted the labelednets directive, so CnlReader
    /// restored an empty LabeledNets → HbEngine skipped __LabeledNodes → picker showed all nodes.
    /// </summary>
    [Fact]
    public void T7_EndToEnd_SchematicCnl_EmitsLabeledNodesCube()
    {
        // Simulate the in-memory state after NetExtractor runs on a schematic with
        // user-labeled nets n_drain and n_gate.
        var (_, tbOriginal) = new CnlReader().Read(SimpleCnl);
        tbOriginal.LabeledNets.Add("n_drain");
        tbOriginal.LabeledNets.Add("n_gate");

        // Write to .cnl text (this is what WorkspaceViewModel.WriteNetlist does).
        var cnlText = CnlWriter.Write(tbOriginal);

        // Read back (this is what SchematicRunService / CnlReader.ReadFile does).
        var (lib, tbRead) = new CnlReader().Read(cnlText);

        // LabeledNets must have survived the round-trip.
        Assert.Contains("n_drain", tbRead.LabeledNets);
        Assert.Contains("n_gate",  tbRead.LabeledNets);

        // Run HB from the re-read TestBench — must emit __LabeledNodes.
        var netlist = new Elaborator(lib).Elaborate(tbRead);
        var hba     = tbRead.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var ds      = (DataSet)new HbEngine(netlist, tbRead).Run(p);

        Assert.True(ds.Contains("__LabeledNodes"),
            "__LabeledNodes cube must be present after CnlWriter→CnlReader round-trip");

        var labels = ds["__LabeledNodes"].Axes[0].Labels!;
        Assert.Contains("n_drain", labels);
        Assert.Contains("n_gate",  labels);
    }

    // ── Stack_MetaCubeNotSwept ────────────────────────────────────────────────

    /// <summary>
    /// After a 3-point sweep, <c>__LabeledNodes.Axes[0].Name</c> must equal "label"
    /// (not "Pin" or any sweep-variable name).
    /// This is the direct regression guard for the prepend-axis bug.
    /// </summary>
    [Fact]
    public void Stack_MetaCubeNotSwept()
    {
        static DataSet MakePoint()
        {
            var nodeVals  = new[] { 0.0 };
            var harmVals  = new[] { 0.0 };
            var nodeAxis  = new Axis("node",     nodeVals, "", new[] { "n_drain" });
            var harmAxis  = new Axis("harmonic", harmVals, "Hz");

            var ds = new DataSet();
            ds.Add("V", new DataCube(new[] { nodeAxis, harmAxis },
                       new System.Numerics.Complex[1]));

            var lblAxis = new Axis("label", new[] { 0.0 }, "", new[] { "n_drain" });
            ds.Add("__LabeledNodes", new DataCube(new[] { lblAxis }, new double[1]));
            return ds;
        }

        var sweepAxis = new Axis("Vgg", new[] { -4.0, -3.5, -3.0 });
        var pts       = new[] { MakePoint(), MakePoint(), MakePoint() };
        var stacked   = DataSet.StackSweepAxis(sweepAxis, pts);

        var lblCube = stacked["__LabeledNodes"];
        Assert.Equal(1, lblCube.Rank);
        Assert.Equal("label", lblCube.Axes[0].Name);
        Assert.NotNull(lblCube.Axes[0].Labels);
    }
}
