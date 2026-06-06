using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Zero-volt series ammeter (IProbe). Stamps a 0 V branch constraint; the branch current
/// is the measurement quantity, accessible via I(probeName) in measurement expressions.
///
/// Semantically identical to a 0 V VoltageSourceModel; separated so the branch current
/// is always available by the probe's instance name rather than a node label.
///
/// .cnl syntax: IProbe:IP1 n_plus n_minus
/// </summary>
public sealed class IProbeModel : ComponentModel
{
    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>Branch index set on the most recent Stamp call. Used by HbLinearBackSolver.</summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int np = c.Nodes[0];
        int nm = c.Nodes[1];

        int br = mna.AddBranch();
        LastBranchIndex = br;

        mna.AddConstraint(br, np, new Complex(+1, 0));
        mna.AddConstraint(br, nm, new Complex(-1, 0));
        mna.AddSourceValue(br, Complex.Zero);   // V = 0 (ideal ammeter)
        mna.AddBranchCurrent(br, np, nm);
    }
}
