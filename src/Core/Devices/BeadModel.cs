using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ferrite bead — a two-terminal linear element whose impedance is the published four-element
/// equivalent of a bead. Engine reference <c>Bead</c>.
///
/// <code>
///   Z(ω) = Rdc + [ jωL  ∥  Rp  ∥  1/(jωCp) ]
/// </code>
///
/// <para><b>Why a bead is not an inductor, and not a series RLC either.</b> The number a data sheet
/// gives — "600 Ω at 100 MHz" — is an IMPEDANCE, and the whole point of the part is that most of it
/// is RESISTIVE at the frequency it is quoted at. That is what makes a bead absorb rather than
/// reflect, and it is why it damps a supply rail where an inductor of the same reactance would ring
/// against the decoupling capacitance. An inductor's loss is zero and a series RLC's is a constant
/// <c>R</c>; a bead's rises from nothing at DC to a maximum at its ferromagnetic resonance and falls
/// again above it. This network reproduces exactly that, with each element standing for a real
/// mechanism:</para>
///
/// <list type="bullet">
/// <item><c>Rdc</c> — the winding's own resistance. It is what the part looks like at DC, and in a
/// power rail it is what sets the drop.</item>
/// <item><c>L</c> — the low-frequency inductance, which is what the impedance rises along.</item>
/// <item><c>Rp</c> — the core loss. It CAPS the impedance: at the parallel resonance the reactive
/// branches cancel and <c>|Z|</c> is <c>Rdc + Rp</c>, which is the peak a data sheet plots. Nothing
/// else in the network sets that peak, so a bead fitted without it has no maximum at all.</item>
/// <item><c>Cp</c> — the parallel (inter-turn) capacitance, which is what takes the impedance back
/// down above resonance. A bead is not a filter above its own resonance, and this is why.</item>
/// </list>
///
/// <para><b>Zero means NOT MODELLED for each of the three parallel elements</b>, never "a short" or
/// "a zero-ohm resistor". <c>Rp = 0</c> removes the loss branch and leaves an ideal <c>L</c>;
/// <c>Cp = 0</c> removes the capacitive branch and the impedance goes on rising; <c>L = 0</c> leaves
/// the part as a plain <c>Rdc</c>. Reading a zero parallel resistance literally would SHORT the
/// tank, which is the opposite of what a card omitting it means.</para>
///
/// <para><b>At DC (ω = 0) the bead is <c>Rdc</c> and nothing else</b>, because the inductive branch
/// shorts the tank out. That is the physically right answer and it is also what a DC operating point
/// needs from this part — a bead in a supply rail must not open it.</para>
///
/// <para><b>What this cannot do:</b> saturation. A bead's inductance falls with DC bias current, by
/// a lot, and that is a nonlinear effect this linear element has no way to express — the parameters
/// describe the part at whatever current they were measured at. It is also why a bead chosen from a
/// small-signal impedance curve can behave quite differently in the rail it was chosen for.</para>
/// </summary>
public sealed class BeadModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double rdc = Read(c, "Rdc");
        double l   = Read(c, "L");
        double rp  = Read(c, "Rp");
        double cp  = Read(c, "Cp");

        var z = new Complex(rdc, 0.0);

        // The parallel tank. At DC the inductive branch is a short, so the tank contributes nothing
        // and the bead is its winding resistance — which is both the physics and what a DC operating
        // point needs, since a bead in a supply rail must not open it.
        if (omega != 0.0 && l > 0)
        {
            Complex y = Complex.One / new Complex(0.0, omega * l);
            if (rp > 0) y += new Complex(1.0 / rp, 0.0);
            if (cp > 0) y += new Complex(0.0, omega * cp);
            z += Complex.One / y;
        }

        // The same branch-constraint shape SeriesRlcModel uses: a Group-2 current unknown, KCL into
        // one node and out of the other, and V_a − V_b − Z·i = 0. It is the form that works for an
        // arbitrary Z(ω), including Z = 0 — a bead with no parameters at all is then a wire, which
        // is a short constraint rather than a division.
        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);
        mna.AddConstraint(br, c.Nodes[0], +Complex.One);
        mna.AddConstraint(br, c.Nodes[1], -Complex.One);
        if (z != Complex.Zero) mna.AddBranchConstraint(br, br, -z);
    }

    /// <summary>
    /// A parameter's real value, or zero when it is absent or is not a number. Zero is the "not
    /// modelled" reading for every one of them, so an absent parameter and a stated zero mean the
    /// same thing — which is what lets a card state only the two or three elements it knows.
    /// </summary>
    private static double Read(ElaboratedComponent c, string name)
        => c.Parameters.TryGetValue(name, out var v) && v.Kind == Expressions.ValueKind.Real
            ? v.AsReal()
            : 0.0;
}
