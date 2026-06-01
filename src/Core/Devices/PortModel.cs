using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// RF port / termination. Stamps as a 0 V voltage source between its two nodes
/// (signal node = Nodes[0], reference node = Nodes[1]).
///
/// During S-parameter analysis the engine overrides the RHS for the driven port to
/// 1 V and reads the resulting branch currents to form the port Y-matrix (§9).
/// The port Z0 is the renormalization impedance — not a physical resistor in the network.
///
/// LastBranchIndex: set each time Stamp is called; stable across frequencies because
/// branch allocation order is deterministic (same topology, same stamp order).
/// </summary>
public sealed class PortModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>
    /// The branch row/col index assigned on the most recent Stamp call.
    /// Used by SParameterEngine to locate port branch currents in the solution vector.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        LastBranchIndex = mna.AddBranch();
        mna.AddBranchCurrent(LastBranchIndex, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(LastBranchIndex, c.Nodes[0], +Complex.One);
        mna.AddConstraint(LastBranchIndex, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(LastBranchIndex, Complex.Zero); // 0 V by default
    }
}

/// <summary>Alias for "Term" in .cnl; identical behaviour to PortModel.</summary>
public sealed class TermModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        LastBranchIndex = mna.AddBranch();
        mna.AddBranchCurrent(LastBranchIndex, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(LastBranchIndex, c.Nodes[0], +Complex.One);
        mna.AddConstraint(LastBranchIndex, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(LastBranchIndex, Complex.Zero);
    }
}
