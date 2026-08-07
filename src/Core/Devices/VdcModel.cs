using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Independent DC voltage source (Group 2 branch-current element).
/// Stamps Va − Vb = Vdc at ω≈0; stamps Va − Vb = 0 (ideal short) at all other ω.
/// Parameter: Vdc = DC voltage (V). Alias: V (for backward compatibility).
/// </summary>
public sealed class VdcModel : ComponentModel
{
    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    private const double OmegaTolRads = 1.0;

    private double _voltage;

    /// <summary>
    /// Matrix index of the branch-current unknown allocated during the most recent
    /// <see cref="Stamp"/> call.  Stable across frequencies (topology-invariant).
    /// −1 before first Stamp call.  Used by <see cref="HarmonicBalance.HbLinearExtractor"/>
    /// to build the branch-index↔name map for export.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    /// <summary>
    /// A supply voltage set on the MODEL rather than resolved from the netlist, overriding the
    /// instance's own <c>Vdc=</c> while it is non-null.
    ///
    /// <para><b>Why this exists.</b> A resolved parameter is fixed at elaboration, so moving a bias
    /// point otherwise means re-elaborating the whole netlist — which harmonicaRF does thousands of
    /// times a second and which <c>harmonicarf.md</c> §6.1 measures at roughly a thousand times the
    /// cost of the thing being changed. This is the same in-place-mutation pattern
    /// <c>TunerModel.SetHarmonicOverride</c> already establishes for a termination.</para>
    ///
    /// <para>Null is the default and the shipped behaviour exactly: the netlist's own value wins and
    /// nothing about a design that never sets this changes.</para>
    /// </summary>
    public double? VdcOverride { get; set; }

    /// <summary>Sets <see cref="VdcOverride"/>. Present so a caller reads as an action, not a field.</summary>
    public void SetVdc(double volts) => VdcOverride = volts;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int va = c.Nodes[0];
        int vb = c.Nodes[1];

        if (c.Parameters.TryGetValue("Vdc", out var vp))
            _voltage = vp.Kind == ValueKind.Real ? vp.AsReal() : vp.AsComplex().Real;
        else if (c.Parameters.TryGetValue("V", out var v))
            _voltage = v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real;

        if (VdcOverride is { } ov) _voltage = ov;

        int br = mna.AddBranch();
        LastBranchIndex = br;
        mna.AddConstraint(br, va, new Complex(+1, 0));
        mna.AddConstraint(br, vb, new Complex(-1, 0));
        mna.AddBranchCurrent(br, va, vb);

        Complex e = Math.Abs(omega) < OmegaTolRads ? new Complex(_voltage, 0) : Complex.Zero;
        mna.AddSourceValue(br, e);
    }
}
