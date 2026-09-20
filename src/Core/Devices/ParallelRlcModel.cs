using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal PARALLEL pair or triple — some subset of an R, an L and a C all across the same two
/// nodes. Engine references <c>PRLC</c>, <c>PRL</c>, <c>PRC</c> and <c>PLC</c>. The lumped tank: a
/// resonator, or the shunt loss-plus-resonance an equivalent circuit is fitted to.
///
/// <para><b>Two of the three stamp as admittances and one does not.</b> R contributes 1/R and C
/// contributes jωC through <c>AddAdmittance</c>, which is what those elements do in parallel. The
/// inductor CANNOT: its admittance 1/(jωL) diverges as ω→0, so a Group-1 stamp has no DC form at
/// all. It instead takes its own Group-2 branch with the constraint V_a − V_b − jωL·i = 0, which at
/// ω = 0 degenerates cleanly to V_a − V_b = 0 — an ideal inductor's DC short, stamped exactly and
/// with no gmin fudge. <see cref="InductorModel"/> makes the same choice for the same reason.</para>
///
/// <para>That branch is also what a <see cref="MutualInductanceModel"/> couples to, so this family's
/// inductor can be one end of a transformer just as a plain inductor can — see
/// <see cref="IInductiveBranch"/>. It carries a bare −jωL diagonal with no R or C mixed into it,
/// because in this topology neither is in series with the coil; the mutual stamp therefore lands on
/// exactly the term it means to. <see cref="ParallelRcModel"/> is the one member of the family with
/// no inductor, and it therefore stamps no branch at all and implements no such interface.</para>
///
/// <para><b>An absent element is an OPEN here</b> — G = 0, C = 0, no branch — which is the dual of
/// the series family's short. See <see cref="RlcElements"/>.</para>
///
/// <para><b>Non-physical inputs warn and continue</b>, matching <see cref="ResistorModel"/> —
/// circuitRF is a research tool and a refusal here would block a legitimate experiment. R = 0 is a
/// dead short across the tank and stamps Gmax; R &lt; 0 stamps a negative conductance with its sign.
/// Both warn once per instance, not once per frequency point.</para>
/// </summary>
public abstract class ParallelRlcBranchModel : ComponentModel, IReportsWarnings
{
    private readonly RlcElements _elements;

    private protected ParallelRlcBranchModel(RlcElements elements) => _elements = elements;

    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>The netlist reference, so a warning names the part the user actually placed.</summary>
    protected abstract string Reference { get; }

    /// <summary>Branch index assigned on the most recent Stamp call, or −1 before the first.</summary>
    /// <remarks>Stays −1 for ever on a member with no inductance — there is no branch to report.</remarks>
    protected int Branch { get; private set; } = -1;

    // Deduplication: warn once per component instance, not once per frequency point.
    private bool _warnedR;

    // Queued rather than printed — see ResistorModel for why; drained by the engine after each stamp.
    private readonly List<(string Key, string Message)> _pending = [];

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        if (_pending.Count == 0) return [];
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    private bool Has(RlcElements e) => (_elements & e) != 0;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int a = c.Nodes[0], b = c.Nodes[1];

        // ── R: Group 1 ────────────────────────────────────────────────────────
        if (Has(RlcElements.R))
        {
            double r = c.Parameters["R"].AsReal();
            double g;
            if (r == 0.0)
            {
                if (!_warnedR)
                {
                    _pending.Add(($"prlc.short:{c.InstancePath}",
                        $"{Reference}:{c.InstancePath}: R=0 Ω — a short across the whole element; " +
                        $"stamping Gmax={ResistorModel.DefaultGmax:G4} S and proceeding. " +
                        "(Set R to a large value for a low-loss tank.)"));
                    _warnedR = true;
                }
                g = ResistorModel.DefaultGmax;
            }
            else if (r < 0.0)
            {
                if (!_warnedR)
                {
                    _pending.Add(($"prlc.negative:{c.InstancePath}",
                        $"{Reference}:{c.InstancePath}: R={r:G4} Ω < 0 — non-physical/active element."));
                    _warnedR = true;
                }
                g = 1.0 / r;   // negative conductance — intentional
            }
            else
            {
                g = 1.0 / r;
            }
            mna.AddAdmittance(a, b, new Complex(g, 0.0));
        }

        // ── C: Group 1. jωC = 0 at DC → exact open, which is what a capacitor is there. ─────
        if (Has(RlcElements.C))
            mna.AddAdmittance(a, b, new Complex(0.0, omega * c.Parameters["C"].AsReal()));

        // ── L: Group 2, its own branch. ───────────────────────────────────────
        if (!Has(RlcElements.L)) return;

        double l  = c.Parameters["L"].AsReal();
        int    br = mna.AddBranch();
        Branch = br;

        mna.AddBranchCurrent(br, a, b);          // KCL: branch current i flows a → b
        mna.AddConstraint(br, a, +Complex.One);  // V_a − V_b − jωL·i = 0
        mna.AddConstraint(br, b, -Complex.One);

        var diag = new Complex(0.0, -omega * l);
        if (diag != Complex.Zero)
            mna.AddBranchConstraint(br, br, diag);
        // diag == 0 (DC, or L = 0) leaves the row as V_a − V_b = 0 — the ideal inductor's short.
    }
}

/// <inheritdoc cref="ParallelRlcBranchModel"/>
/// <summary>R, L and C in parallel. Engine reference <c>PRLC</c>.</summary>
public sealed class ParallelRlcModel() : ParallelRlcBranchModel(RlcElements.Rlc), IInductiveBranch
{
    /// <inheritdoc/>
    protected override string Reference => "PRLC";

    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}

/// <summary>
/// R in parallel with L — engine reference <c>PRL</c>. The shunt form of a lossy coil, and the usual
/// way a measured low-Q inductance is entered: R sets the loss, and at DC the ideal inductor shorts
/// it out exactly as the physical part does.
/// </summary>
/// <remarks>See <see cref="ParallelRlcBranchModel"/> for the shared stamp and the Mutual contract.</remarks>
public sealed class ParallelRlModel() : ParallelRlcBranchModel(RlcElements.Rl), IInductiveBranch
{
    /// <inheritdoc/>
    protected override string Reference => "PRL";

    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}

/// <summary>
/// R in parallel with C — engine reference <c>PRC</c>. A leaky capacitor, or the shunt RC an
/// equivalent circuit puts across a port.
/// </summary>
/// <remarks>
/// <b>Two admittances and NO branch at all</b>, which makes it the only member of this family that
/// adds no unknown to the matrix. It is therefore not an <see cref="IInductiveBranch"/> — there is
/// nothing for a <see cref="MutualInductanceModel"/> to couple to, and saying so at the reference is
/// what turns a wrong Mutual into a refusal that names the part instead of a silent coupling into a
/// diagonal that is not an inductance.
/// </remarks>
public sealed class ParallelRcModel() : ParallelRlcBranchModel(RlcElements.Rc)
{
    /// <inheritdoc/>
    protected override string Reference => "PRC";
}

/// <summary>
/// L in parallel with C — engine reference <c>PLC</c>. The lossless tank: infinite impedance at
/// 1/(2π√(LC)), a short at DC.
/// </summary>
/// <remarks>See <see cref="ParallelRlcBranchModel"/> for the shared stamp and the Mutual contract.</remarks>
public sealed class ParallelLcModel() : ParallelRlcBranchModel(RlcElements.Lc), IInductiveBranch
{
    /// <inheritdoc/>
    protected override string Reference => "PLC";

    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}
