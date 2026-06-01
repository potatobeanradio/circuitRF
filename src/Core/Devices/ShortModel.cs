using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal short circuit between two nodes (0 V voltage source branch).
/// Used to model ideal wire connections and AC current probes.
/// Group 2: one branch-current unknown, constraint V_a − V_b = 0.
/// </summary>
public sealed class ShortModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(br, c.Nodes[0], +Complex.One);
        mna.AddConstraint(br, c.Nodes[1], -Complex.One);
        mna.AddSourceValue(br, Complex.Zero);  // V = 0
    }
}
