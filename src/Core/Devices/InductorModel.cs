using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal inductor. Group 2 (branch-current unknown).
/// Constraint: V_a − V_b − jωL·i = 0
/// At DC (ω = 0): constraint reduces to V_a = V_b → exact short circuit.
/// </summary>
public sealed class InductorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// Branch index assigned on the most recent Stamp call.
    /// Set during each frequency pass; stable across frequencies for a fixed topology.
    /// Used by MutualInductanceModel to stamp off-diagonal coupling terms.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double l  = c.Parameters["L"].AsReal();
        int    br = mna.AddBranch();
        LastBranchIndex = br;

        // KCL: branch current i flows from Nodes[0] to Nodes[1]
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);

        // Constraint: V_a - V_b - jωL·i = 0
        mna.AddConstraint(br, c.Nodes[0], +Complex.One);
        mna.AddConstraint(br, c.Nodes[1], -Complex.One);
        // −jωL term on the branch current column (in the lower-right D block)
        // At DC (ω=0) this term vanishes → constraint is V_a − V_b = 0 (exact short)
        if (omega != 0.0)
            mna.AddBranchConstraint(br, br, new Complex(0.0, -omega * l));
    }
}
