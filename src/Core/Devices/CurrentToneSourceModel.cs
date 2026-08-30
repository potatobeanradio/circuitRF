using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Single- or multi-tone ideal CURRENT source — the dual of <see cref="ToneSourceModel"/>.
/// One internal model, two netlist spellings: I_1Tone and I_nTone.
///
/// Group 1 element: it adds NO unknown and NO constraint row, only a right-hand-side injection
///   ω = 0         → J = Idc  (DC bias, may be zero)
///   ω ≈ 2π·Freq_i → J = phasor_i (I * exp(j·Phase_i·π/180))
///   otherwise     → J = 0  (OPEN at non-excited frequencies)
///
/// <para><b>Direction — the symbol's arrow is this sentence.</b> J is injected INTO the FIRST node
/// and drawn out of the second, which is the engine's fixed current-source convention
/// (<c>src/Engine/CLAUDE.md</c> → "Current-source direction"). A positive <c>I</c> therefore
/// DELIVERS current to pin 1, and the ITone glyph's arrowhead points at pin 1 to say so. Note this
/// is the opposite of the SPICE <c>I</c>-element, which sinks current from its first node —
/// deliberately, because one convention across circuitRF's sources beats agreement with another
/// tool's; a sign flip here is invisible in results that still look plausible.</para>
///
/// <para><b>Not a control-current reference.</b> A current source has no branch-current unknown to
/// point at — its current is an input, not a solution — so it is absent from the SDD
/// <c>C[n]=&lt;instance&gt;</c> resolvers on purpose, unlike its voltage counterpart.</para>
///
/// <para><b>An ideal current source is an OPEN off its tones.</b> A node driven only by one has no
/// DC path to ground and makes the matrix singular; that is the physics of the element, and the
/// engine's own zero-row diagnostic names the offending node.</para>
/// </summary>
public sealed class CurrentToneSourceModel : ToneSourceModelBase
{
    public CurrentToneSourceModel(ToneEntry[] tones, double idcResolved,
        Expr? idcExpr = null, IReadOnlyDictionary<string, Value>? idcScopeVars = null)
        : base(tones, idcResolved, idcExpr, idcScopeVars) { }

    protected override string DcParamName => "Idc";

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;

        Complex j = ExcitationAt(omega);
        if (j == Complex.Zero) return;   // an unexcited ideal current source contributes nothing

        mna.AddCurrentInjection(c.Nodes[0], +j);
        mna.AddCurrentInjection(c.Nodes[1], -j);
    }
}
