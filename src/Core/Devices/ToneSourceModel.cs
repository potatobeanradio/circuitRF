using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Single- or multi-tone ideal VOLTAGE source (linear-engine §4.4).
/// One internal model, two netlist spellings: V_1Tone and V_nTone.
///
/// Group 2 branch-current element. Stamps constraint Va − Vb = E(ω):
///   ω = 0         → E = Vdc  (DC bias, may be zero)
///   ω ≈ 2π·Freq_i → E = phasor_i (V * exp(j·Phase_i·π/180))
///   otherwise     → E = 0  (short at non-excited frequencies)
///
/// The tone table, the DC offset and the sweep-time re-evaluation of V/Vdc expressions live in
/// <see cref="ToneSourceModelBase"/>, shared with the current source; only the stamp is here.
/// </summary>
public sealed class ToneSourceModel : ToneSourceModelBase
{
    /// <summary>
    /// Branch index for the tone source's current (set each Stamp). −1 before first stamp.
    /// Mirrors VdcModel.LastBranchIndex — exposes the source current as a referenceable
    /// control-current branch (SDD C[n]=&lt;toneSrc&gt;).
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public ToneSourceModel(ToneEntry[] tones, double vdcResolved,
        Expr? vdcExpr = null, IReadOnlyDictionary<string, Value>? vdcScopeVars = null)
        : base(tones, vdcResolved, vdcExpr, vdcScopeVars) { }

    protected override string DcParamName => "Vdc";

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int va = c.Nodes[0];
        int vb = c.Nodes[1];

        int br = LastBranchIndex = mna.AddBranch();
        mna.AddConstraint(br, va, new Complex(+1, 0));
        mna.AddConstraint(br, vb, new Complex(-1, 0));
        mna.AddBranchCurrent(br, va, vb);
        mna.AddSourceValue(br, ExcitationAt(omega));
    }
}
