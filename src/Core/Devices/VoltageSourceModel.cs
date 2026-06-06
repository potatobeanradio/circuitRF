using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Independent DC/AC voltage source (Group 2 branch-current element).
/// Stamps: Va − Vb = V (branch constraint); branch current flows from Va to Vb.
/// Parameter: V = voltage (V).
/// </summary>
public sealed class VoltageSourceModel : ComponentModel
{
    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    public void Resolve(IReadOnlyDictionary<string, Value> parameters)
    {
        if (parameters.TryGetValue("V", out var v) && v.Kind == ValueKind.Real)
            _voltage = v.AsReal();
        else if (parameters.TryGetValue("V", out var vc) && vc.Kind == ValueKind.Complex)
            _voltage = vc.AsComplex().Real;
    }

    private double _voltage;

    /// <summary>
    /// Matrix index of the branch-current unknown allocated during the most recent
    /// <see cref="Stamp"/> call.  Stable across frequencies (topology-invariant).
    /// −1 before first Stamp call.  Used by <see cref="HarmonicBalance.HbLinearExtractor"/>
    /// to build the branch-index↔name map for export.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int va = c.Nodes[0];
        int vb = c.Nodes[1];

        // Resolve voltage from parameters if not already done.
        if (c.Parameters.TryGetValue("V", out var param))
        {
            _voltage = param.Kind == ValueKind.Real
                ? param.AsReal()
                : param.AsComplex().Real;
        }

        int br = mna.AddBranch();
        LastBranchIndex = br;
        // Constraint row: Va − Vb − V = 0  →  Va − Vb = V
        mna.AddConstraint(br, va, new Complex(+1, 0));
        mna.AddConstraint(br, vb, new Complex(-1, 0));
        mna.AddSourceValue(br, new Complex(_voltage, 0));
        // KCL: branch current flows from Va to Vb
        mna.AddBranchCurrent(br, va, vb);
    }
}
