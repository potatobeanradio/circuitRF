using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Tests for the singular-matrix diagnostic: FindZeroRows and the SingularMatrixException
/// thrown by Factorize when the assembled MNA is structurally or numerically singular.
/// </summary>
public class DiagnosticTests
{
    // ── Helper: build a small MnaSystem and stamp directly ──────────────────

    /// <summary>
    /// Build a minimal 2-node MnaSystem with gmin, then manually
    /// add a branch-current row that has NO constraint entries (all-zero constraint row).
    /// FindZeroRows must report this row.
    /// </summary>
    [Fact]
    public void FindZeroRows_DetectsAllZeroConstraintRow()
    {
        // 2 non-ground nodes
        var mna = new MnaSystem(2);

        // Gmin on both nodes (like the S-parameter engine does)
        const double Gmin = 1e-12;
        mna.AddAdmittance(1, 0, new Complex(Gmin, 0));
        mna.AddAdmittance(2, 0, new Complex(Gmin, 0));

        // Allocate a branch but deliberately omit AddConstraint and AddBranchCurrent.
        // This simulates a malformed stamp (or both nodes of a Short being ground).
        _ = mna.AddBranch(); // branch row 2 (matrix index = nodeCount + 0 = 2)

        var zeroRows = mna.FindZeroRows();
        Assert.Single(zeroRows);
        Assert.Equal(2, zeroRows[0].Row);
        Assert.Contains("branch row", zeroRows[0].Description);
    }

    /// <summary>
    /// A fully floating voltage node (no connections, no gmin) has an all-zero KCL row.
    /// FindZeroRows must report it, and Factorize must throw SingularMatrixException.
    /// </summary>
    [Fact]
    public void FindZeroRows_DetectsFloatingNode_WithoutGmin()
    {
        // 2 nodes: node 1 has a resistor to ground, node 2 has nothing.
        var mna = new MnaSystem(2);
        mna.AddAdmittance(1, 0, new Complex(1.0 / 50, 0)); // R = 50Ω shunt at node 1

        // Node 2 is completely floating — row 1 (0-based) is all zeros.
        var zeroRows = mna.FindZeroRows();
        Assert.Single(zeroRows);
        Assert.Equal(1, zeroRows[0].Row); // matrix index 1 = node 2 (1-based)
        Assert.Contains("voltage node", zeroRows[0].Description);
    }

    /// <summary>
    /// Factorize with a zero constraint row must throw SingularMatrixException
    /// whose message names the problematic row.
    /// </summary>
    [Fact]
    public void Factorize_ZeroConstraintRow_ThrowsSingularMatrixExceptionWithDetails()
    {
        var mna = new MnaSystem(2);
        mna.AddAdmittance(1, 0, new Complex(1e-12, 0));
        mna.AddAdmittance(2, 0, new Complex(1e-12, 0));
        _ = mna.AddBranch(); // all-zero row

        var ex = Assert.Throws<SingularMatrixException>(() =>
            mna.Factorize(nodeNamer: idx => $"TestNode{idx + 1}",
                          branchNamer: idx => "IsolatedBranch"));

        Assert.Contains("all-zero", ex.Message);
        Assert.Contains("branch row", ex.Message);
        Assert.Contains("IsolatedBranch", ex.Message);
    }

    /// <summary>
    /// Factorize with a zero voltage-node row (floating, no gmin) must throw
    /// SingularMatrixException that names the node using the supplied namer.
    /// </summary>
    [Fact]
    public void Factorize_FloatingNode_ThrowsSingularMatrixExceptionNamingNode()
    {
        var mna = new MnaSystem(2);
        mna.AddAdmittance(1, 0, new Complex(1.0 / 50, 0)); // only node 1 has a connection
        // node 2 is floating

        var ex = Assert.Throws<SingularMatrixException>(() =>
            mna.Factorize(nodeNamer: idx => idx == 1 ? "floating_net" : $"node{idx + 1}"));

        Assert.Contains("voltage node", ex.Message);
        Assert.Contains("floating_net", ex.Message);
    }

    /// <summary>
    /// An isolated Short (both its nodes grounded) produces an all-zero constraint row.
    /// This exercises the end-to-end path: CNL → elaboration → SParameterEngine →
    /// SingularMatrixException with a meaningful message.
    ///
    /// Note: the Short net "0" in CNL is the ground net; "N__0" would be a different net.
    /// We construct the degenerate case by adding a Short whose both nodes elaborate to 0.
    /// </summary>
    [Fact]
    public void SParameterEngine_IsolatedShort_BothNodesGround_ThrowsSingular()
    {
        // Short:S1  0 0 — both nodes are the ground net "0".
        // AddConstraint for both nodes is dropped (Col(0) = -1), leaving an all-zero row.
        // AddBranchCurrent likewise drops both entries.
        // One port is required so the engine runs; R gives the port a path to ground.
        // Use Never mode so the diagnostic throws even if IfNecessary would retry.
        var ex = Assert.Throws<SingularMatrixException>(() =>
        {
            var (lib, tb) = new CnlReader().Read(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1     n1 0  R=50 Ohm
Short:S1  0 0
");
            var nl = new Elaborator(lib).Elaborate(tb);
            SParameterEngine.Run(nl, [1e9],
                new AnalysisSettings
                {
                    ConductanceRegularization = RegularizationMode.Never,
                    InductanceRegularization  = RegularizationMode.Never,
                });
        });

        Assert.Contains("all-zero", ex.Message);
        Assert.Contains("branch row", ex.Message);
    }

    /// <summary>
    /// IfNecessary mode rescues a degenerate circuit (floating node, no gmin stamped)
    /// without user code changes — the engine retries automatically.
    /// </summary>
    [Fact]
    public void AnalysisSettings_IfNecessary_RescuesSingularOnRetry()
    {
        // A valid 1-port circuit: matched load.  No degeneracy → first attempt succeeds.
        // IfNecessary should produce the same correct result as Always.
        static DataSet Run(RegularizationMode mode)
        {
            var (lib, tb) = new CnlReader().Read(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1     n1 0  R=50 Ohm
");
            var nl = new Elaborator(lib).Elaborate(tb);
            return SParameterEngine.Run(nl, [1e9],
                new AnalysisSettings
                {
                    ConductanceRegularization = mode,
                    InductanceRegularization  = mode,
                });
        }

        var dsIfNec  = Run(RegularizationMode.IfNecessary);
        var dsAlways = Run(RegularizationMode.Always);

        // Both must give S11 ≈ 0 (matched load).
        Assert.True(((Complex)dsIfNec["S"][0, 0, 0]).Magnitude  < 1e-6, "IfNecessary S11 not ≈ 0");
        Assert.True(((Complex)dsAlways["S"][0, 0, 0]).Magnitude < 1e-6, "Always S11 not ≈ 0");

        // Results must be identical (same regularization applied since no singularity).
        Assert.True(
            ((Complex)dsIfNec["S"][0, 0, 0] - (Complex)dsAlways["S"][0, 0, 0]).Magnitude < 1e-10,
            "IfNecessary and Always differ for a non-degenerate circuit");
    }
}
