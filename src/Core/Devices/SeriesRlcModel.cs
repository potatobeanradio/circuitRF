using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal series RLC branch — R, L and C in series on a single Group-2 branch-current
/// unknown. Engine reference <c>SRLC</c>.
///
/// Constraint: V_a − V_b − Z(ω)·i = 0, with Z(ω) = R + jωL + 1/(jωC).
///
/// <para><b>Why this exists when <see cref="InductorModel"/> already accepts optional R= and C=.</b>
/// It is the same arithmetic, and deliberately so — see the shared branch below. What differs is
/// everything around it: the part shows a series-RLC glyph rather than a coil, all three values are
/// required and shown, and the netlist says <c>SRLC</c>, which is what a reader needs to see. The
/// most common reason to place one is a real ceramic capacitor whose vendor states an ESR and an
/// ESL — a component that IS a series RLC, and which as three wired elements is three instance
/// names, three parameter rows and a schematic that no longer looks like the bill of materials.</para>
///
/// <para><b>DC behaviour (ω = 0).</b> The series capacitance makes the branch a DC OPEN: 1/(jωC)
/// diverges as ω→0, so the constraint is stamped as −i = 0, forcing the branch current to zero and
/// leaving the node voltages unconstrained by this branch. That is the same treatment
/// <see cref="InductorModel"/> gives its optional C=, and it is what the DC and HB engines already
/// expect from a branch that opens at DC.</para>
///
/// <para><b>The inductance sits on the branch diagonal</b>, so a <see cref="MutualInductanceModel"/>
/// couples to an SRLC exactly as it couples to a plain inductor — see
/// <see cref="IInductiveBranch"/>. The R and C terms live on the same diagonal and are simply not
/// what the mutual stamp touches.</para>
/// </summary>
public sealed class SeriesRlcModel : ComponentModel, IInductiveBranch
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <inheritdoc/>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double r   = c.Parameters["R"].AsReal();
        double l   = c.Parameters["L"].AsReal();
        double cap = c.Parameters["C"].AsReal();

        int br = mna.AddBranch();
        LastBranchIndex = br;

        // A zero series capacitance is an infinite impedance at every frequency — an open branch,
        // at DC and above alike. Stamped as the DC-open case rather than dividing by zero.
        bool open = omega == 0.0 || cap == 0.0;

        if (open)
        {
            // DC (or C=0): the series capacitor is an open circuit. KCL column so the branch column
            // is non-zero, then the constraint −i = 0. Node voltages are unconstrained by this
            // branch, which is what an open circuit means.
            mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);
            mna.AddBranchConstraint(br, br, new Complex(-1.0, 0.0));
            return;
        }

        // AC: KCL — branch current i flows from Nodes[0] to Nodes[1].
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);

        // Constraint: V_a − V_b − Z·i = 0
        mna.AddConstraint(br, c.Nodes[0], +Complex.One);
        mna.AddConstraint(br, c.Nodes[1], -Complex.One);

        // −Z = −R − jωL − 1/(jωC) = −R + j(1/(ωC) − ωL). The capacitive term enters with a PLUS
        // sign on the imaginary part because −1/(jωC) = +j/(ωC).
        var diag = new Complex(-r, -omega * l + 1.0 / (omega * cap));
        if (diag != Complex.Zero)
            mna.AddBranchConstraint(br, br, diag);
    }
}
