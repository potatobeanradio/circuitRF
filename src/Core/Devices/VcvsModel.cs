using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal voltage-controlled voltage source (the "E element" of the SPICE family), linear and
/// frequency-independent.
///
///   V(out+) − V(out−) = E · (V(ctrl+) − V(ctrl−))
///
/// <para><b>Four terminals, two ports, in the repository's own ± pair order</b> — the same 2N-net
/// convention <see cref="VccsModel"/>, <c>Z_Port</c> and the SDD use, so <c>PortCount</c> is 2 and
/// <c>Nodes = [out+, out−, ctrl+, ctrl−]</c>. Port 0 is the OUTPUT (the pair whose voltage is
/// constrained) and port 1 is the CONTROL, which draws no current at all — that is what makes the
/// source ideal, and it is the entry a wrong stamp adds.</para>
///
/// <para><b>Group 2, unlike the VCCS, and that is not a detail.</b> A controlled CURRENT source
/// moves onto the left-hand side of two KCL rows as four admittance entries and needs no new
/// unknown. A controlled VOLTAGE source states a relation BETWEEN node voltages, which no
/// combination of admittances expresses: it needs a branch-current unknown of its own, exactly as
/// <see cref="VdcModel"/> does, and its own constraint row. There is no honest Norton equivalent —
/// one needs a finite source impedance the netlist did not state, and inventing one changes the
/// circuit while leaving it perfectly solvable.</para>
///
/// <para><b>The stamp, in full.</b> With branch current <c>i</c> flowing out+ → out−:</para>
/// <code>
///   KCL:        +1 at (out+, br)   −1 at (out−, br)
///   constraint: +1 at (br, out+)   −1 at (br, out−)
///               −E at (br, ctrl+)  +E at (br, ctrl−)
///   RHS:         0
/// </code>
/// <para>There are no entries in the CONTROL rows — a control row entry would draw current through
/// the sense pair, which is the bug this comment exists to prevent.</para>
///
/// <para><b>Frequency-independent, unlike <see cref="VdcModel"/>.</b> A Vdc is a bias and becomes an
/// ideal short away from DC; a VCVS is a TRANSFER and holds at every frequency, so nothing here
/// looks at ω. A gain that shorted itself above DC would give an S-parameter run a silently
/// different circuit from the one the DC solve saw.</para>
/// </summary>
public sealed class VcvsModel : ComponentModel
{
    public override int       PortCount => 2;   // [0] = output pair, [1] = control pair
    public override ModelKind Kind      => ModelKind.Linear;

    public override string[] TerminalNames => ["out+", "out-", "ctrl+", "ctrl-"];

    /// <summary>
    /// Matrix index of the branch-current unknown allocated during the most recent
    /// <see cref="Stamp"/> call, so a control-current reference can name this source's branch the
    /// same way it names a <see cref="VdcModel"/>'s. −1 before the first stamp.
    /// </summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 4)
            throw new InvalidOperationException(
                $"VCVS '{c.InstancePath}': expected 4 nets (out+, out−, ctrl+, ctrl−); "
                + $"got {c.Nodes.Length}.");

        int op = c.Nodes[0], om = c.Nodes[1], cp = c.Nodes[2], cm = c.Nodes[3];

        double gain = 1.0;
        if (c.Parameters.TryGetValue("E", out var e))
            gain = e.Kind == ValueKind.Real ? e.AsReal() : e.AsComplex().Real;
        else if (c.Parameters.TryGetValue("Gain", out var g))
            gain = g.Kind == ValueKind.Real ? g.AsReal() : g.AsComplex().Real;

        int br = mna.AddBranch();
        LastBranchIndex = br;

        mna.AddBranchCurrent(br, op, om);
        mna.AddConstraint(br, op, new Complex(+1, 0));
        mna.AddConstraint(br, om, new Complex(-1, 0));
        mna.AddConstraint(br, cp, new Complex(-gain, 0));
        mna.AddConstraint(br, cm, new Complex(+gain, 0));
        mna.AddSourceValue(br, Complex.Zero);
    }
}
