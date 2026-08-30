using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal voltage-controlled current source (the "G element" of the SPICE family), linear and
/// frequency-independent.
///
///   I = G · (V(c+) − V(c−))
///
/// <para><b>Four terminals, two ports, in the repository's own ± pair order</b> — the same 2N-net
/// convention <c>Z_Port</c> and <c>SDD</c> use, so <c>PortCount</c> is 2 and
/// <c>Nodes = [out+, out−, ctrl+, ctrl−]</c>. Port 0 is the OUTPUT (where the current flows) and
/// port 1 is the CONTROL (which draws no current at all — it is a pure sense pair, and that is what
/// makes the source ideal).</para>
///
/// <para><b>Direction — the symbol's arrow is this sentence.</b> A positive <c>G</c> and a positive
/// control voltage make current flow DOWN through the source: in at <c>out+</c>, out at <c>out−</c>.
/// The glyph's arrowhead points at <c>out−</c> (the bottom pin as drawn) to say so, and this is the
/// SPICE <c>G</c> element's own direction — it is the way a small-signal transconductance is drawn
/// in every device model, where the controlled source sinks drain current from the drain node.
/// Consequences worth stating, because each one still looks plausible with the sign reversed: a VCCS
/// across a grounded load resistor is INVERTING, and a 50 Ω, G = 10 mS stage measures
/// S21 = −0.25.</para>
///
/// <para><b>This differs from <see cref="CurrentToneSourceModel"/> on purpose.</b> ITone is an
/// INDEPENDENT source and follows the engine's <c>AddCurrentInjection</c> convention (J into the
/// first node, arrow up); the VCCS never calls that API — it stamps admittance — and is drawn the
/// way a controlled source is conventionally drawn. Read each device's own arrow; do not carry one
/// over to the other.</para>
///
/// <para><b>Group 1, so the stamp is four entries and no new unknown.</b> Moving the controlled
/// current to the left-hand side of the two output KCL rows gives, for rows r ∈ {out+, out−} and
/// columns c ∈ {ctrl+, ctrl−}:</para>
/// <code>
///   Y[out+, c+] += G      Y[out+, c-] -= G
///   Y[out-, c+] -= G      Y[out-, c-] += G
/// </code>
/// <para>There are no entries in the control rows — a control row entry would draw current through
/// the sense pair and is exactly the bug this comment exists to prevent.</para>
///
/// <para><b>An unmatched output pair is an open circuit.</b> The output nodes get no diagonal
/// admittance of their own (an ideal current source has infinite output impedance), so an output
/// node with nothing else attached to it has no DC path to ground and makes the matrix singular.
/// That is the element's physics, not a defect; the engine's zero-row diagnostic names the node.</para>
/// </summary>
public sealed class VccsModel : ComponentModel
{
    public override int       PortCount => 2;   // [0] = output pair, [1] = control pair
    public override ModelKind Kind      => ModelKind.Linear;

    public override string[] TerminalNames => ["out+", "out-", "ctrl+", "ctrl-"];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 4)
            throw new InvalidOperationException(
                $"VCCS '{c.InstancePath}': expected 4 nets (out+, out−, ctrl+, ctrl−); " +
                $"got {c.Nodes.Length}.");

        double g = c.Parameters.TryGetValue("G", out var gv) && gv.Kind == ValueKind.Real
                   ? gv.AsReal() : 0.0;
        if (g == 0.0) return;   // G=0 is an open circuit, not a stamp of zeros

        int op = c.Nodes[0], om = c.Nodes[1];   // output +, −
        int cp = c.Nodes[2], cm = c.Nodes[3];   // control +, −

        var gc = new Complex(g, 0);
        mna.AddBlockAdmittance(op, cp, +gc);   // current flows IN at out+ …
        mna.AddBlockAdmittance(op, cm, -gc);
        mna.AddBlockAdmittance(om, cp, -gc);   // … and OUT at out−: down through the source.
        mna.AddBlockAdmittance(om, cm, +gc);
    }
}
