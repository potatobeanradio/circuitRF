using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal parallel RLC branch — R, L and C all across the same two nodes. Engine reference
/// <c>PRLC</c>. The lumped tank: a resonator, or the shunt loss-plus-resonance an equivalent
/// circuit is fitted to.
///
/// <para><b>Two of the three stamp as admittances and one does not.</b> R contributes 1/R and C
/// contributes jωC through <c>AddAdmittance</c>, which is what those elements do in parallel. The
/// inductor CANNOT: its admittance 1/(jωL) diverges as ω→0, so a Group-1 stamp has no DC form at
/// all. It instead takes its own Group-2 branch with the constraint V_a − V_b − jωL·i = 0, which at
/// ω = 0 degenerates cleanly to V_a − V_b = 0 — an ideal inductor's DC short, stamped exactly and
/// with no gmin fudge. <see cref="InductorModel"/> makes the same choice for the same reason.</para>
///
/// <para>That branch is also what a <see cref="MutualInductanceModel"/> couples to, so a PRLC's
/// inductor can be one end of a transformer just as a plain inductor can — see
/// <see cref="IInductiveBranch"/>. It carries a bare −jωL diagonal with no R or C mixed into it,
/// because in this topology neither is in series with the coil; the mutual stamp therefore lands on
/// exactly the term it means to.</para>
///
/// <para><b>Non-physical inputs warn and continue</b>, matching <see cref="ResistorModel"/> —
/// circuitRF is a research tool and a refusal here would block a legitimate experiment. R = 0 is a
/// dead short across the tank and stamps Gmax; R &lt; 0 stamps a negative conductance with its sign.
/// Both warn once per instance, not once per frequency point.</para>
/// </summary>
public sealed class ParallelRlcModel : ComponentModel, IInductiveBranch
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <inheritdoc/>
    public int LastBranchIndex { get; private set; } = -1;

    // Deduplication: warn once per component instance, not once per frequency point.
    private bool _warnedR;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double r   = c.Parameters["R"].AsReal();
        double l   = c.Parameters["L"].AsReal();
        double cap = c.Parameters["C"].AsReal();

        int a = c.Nodes[0], b = c.Nodes[1];

        // ── R: Group 1 ────────────────────────────────────────────────────────
        double g;
        if (r == 0.0)
        {
            if (!_warnedR)
            {
                Console.Error.WriteLine(
                    $"[circuitRF] PRLC:{c.InstancePath}: R=0 Ω — a short across the whole element; " +
                    $"stamping Gmax={ResistorModel.DefaultGmax:G4} S and proceeding. " +
                    "(Set R to a large value for a low-loss tank.)");
                _warnedR = true;
            }
            g = ResistorModel.DefaultGmax;
        }
        else if (r < 0.0)
        {
            if (!_warnedR)
            {
                Console.Error.WriteLine(
                    $"[circuitRF] PRLC:{c.InstancePath}: R={r:G4} Ω < 0 — non-physical/active element; " +
                    "stamping 1/R with its sign and proceeding.");
                _warnedR = true;
            }
            g = 1.0 / r;   // negative conductance — intentional
        }
        else
        {
            g = 1.0 / r;
        }
        mna.AddAdmittance(a, b, new Complex(g, 0.0));

        // ── C: Group 1. jωC = 0 at DC → exact open, which is what a capacitor is there. ─────
        mna.AddAdmittance(a, b, new Complex(0.0, omega * cap));

        // ── L: Group 2, its own branch. ───────────────────────────────────────
        int br = mna.AddBranch();
        LastBranchIndex = br;

        mna.AddBranchCurrent(br, a, b);          // KCL: branch current i flows a → b
        mna.AddConstraint(br, a, +Complex.One);  // V_a − V_b − jωL·i = 0
        mna.AddConstraint(br, b, -Complex.One);

        var diag = new Complex(0.0, -omega * l);
        if (diag != Complex.Zero)
            mna.AddBranchConstraint(br, br, diag);
        // diag == 0 (DC, or L = 0) leaves the row as V_a − V_b = 0 — the ideal inductor's short.
    }
}
