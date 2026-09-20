using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Which of R, L and C a lumped RLC part actually carries. The family is one piece of arithmetic
/// with elements left out, not six pieces of arithmetic that happen to agree.
/// </summary>
/// <remarks>
/// <b>An absent element contributes nothing, and each family writes that in its own terms.</b> In a
/// SERIES branch an absent element is a short — R = 0, L = 0, and no 1/(jωC) term at all — while in
/// a PARALLEL pair it is an open — G = 0, C = 0, and no inductor branch. Writing the absence that
/// way is what keeps <see cref="SeriesRlcBranchModel"/> and <see cref="ParallelRlcBranchModel"/>
/// single-copy: the SRLC stamp IS the SRL stamp with L ∈ elements and C not.
///
/// <para><b>The capacitive term is dropped, not given an infinite C.</b> C = ∞ is the tempting
/// spelling of "a series short" and it reads correctly at every ω but one: at DC, <c>0 * ∞</c> is
/// NaN in IEEE arithmetic, so an SRL's branch diagonal came back (−R, NaN) and the factorization
/// found no pivot. Measured, not reasoned about — <c>TwoElementRlcTests</c>' DC row is the one that
/// caught it.</para>
///
/// <para>The flag is read in one other place, the DC OPEN: a series C makes the branch an open
/// circuit at ω = 0, and a series branch with no C is simply R there.</para>
/// </remarks>
[Flags]
public enum RlcElements
{
    /// <summary>The resistance, parameter <c>R</c>.</summary>
    R = 1,
    /// <summary>The inductance, parameter <c>L</c>.</summary>
    L = 2,
    /// <summary>The capacitance, parameter <c>C</c>.</summary>
    C = 4,

    /// <summary>R and L — <c>SRL</c> / <c>PRL</c>.</summary>
    Rl = R | L,
    /// <summary>R and C — <c>SRC</c> / <c>PRC</c>.</summary>
    Rc = R | C,
    /// <summary>L and C — <c>SLC</c> / <c>PLC</c>.</summary>
    Lc = L | C,
    /// <summary>All three — <c>SRLC</c> / <c>PRLC</c>.</summary>
    Rlc = R | L | C,
}

/// <summary>
/// Two-terminal SERIES branch carrying some subset of an R, an L and a C on a single Group-2
/// branch-current unknown. Engine references <c>SRLC</c>, <c>SRL</c>, <c>SRC</c> and <c>SLC</c>.
///
/// Constraint: V_a − V_b − Z(ω)·i = 0, with Z(ω) = R + jωL + 1/(jωC) over the elements present.
///
/// <para><b>Why this exists when <see cref="InductorModel"/> already accepts optional R= and C=.</b>
/// It is the same arithmetic, and deliberately so — see the shared branch below. What differs is
/// everything around it: the part shows a series glyph rather than a coil, every value it carries is
/// required and shown, and the netlist says <c>SRLC</c> (or <c>SRL</c>, …), which is what a reader
/// needs to see. The most common reason to place one is a real ceramic capacitor whose vendor states
/// an ESR and an ESL — a component that IS a series RLC, and which as three wired elements is three
/// instance names, three parameter rows and a schematic that no longer looks like the bill of
/// materials.</para>
///
/// <para><b>DC behaviour (ω = 0).</b> A branch CARRYING a series capacitance is a DC OPEN: 1/(jωC)
/// diverges as ω→0, so the constraint is stamped as −i = 0, forcing the branch current to zero and
/// leaving the node voltages unconstrained by this branch. That is the same treatment
/// <see cref="InductorModel"/> gives its optional C=, and it is what the DC and HB engines already
/// expect from a branch that opens at DC. A branch with no C (<c>SRL</c>) has an ordinary DC
/// impedance of R and is stamped normally.</para>
///
/// <para><b>The inductance sits on the branch diagonal</b>, so a <see cref="MutualInductanceModel"/>
/// couples to one of these exactly as it couples to a plain inductor — see
/// <see cref="IInductiveBranch"/>. The R and C terms live on the same diagonal and are simply not
/// what the mutual stamp touches. <see cref="SeriesRcModel"/> is the one member of this family that
/// does NOT implement that interface, because it has no inductor to couple to.</para>
/// </summary>
public abstract class SeriesRlcBranchModel : ComponentModel
{
    private readonly RlcElements _elements;

    private protected SeriesRlcBranchModel(RlcElements elements) => _elements = elements;

    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// <summary>Branch index assigned on the most recent Stamp call, or −1 before the first.</summary>
    protected int Branch { get; private set; } = -1;

    private bool Has(RlcElements e) => (_elements & e) != 0;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        // An absent element is the value that contributes nothing in SERIES — see RlcElements.
        bool   hasC = Has(RlcElements.C);
        double r    = Has(RlcElements.R) ? c.Parameters["R"].AsReal() : 0.0;
        double l    = Has(RlcElements.L) ? c.Parameters["L"].AsReal() : 0.0;
        double cap  = hasC ? c.Parameters["C"].AsReal() : 0.0;

        int br = mna.AddBranch();
        Branch = br;

        // A zero series capacitance is an infinite impedance at every frequency — an open branch,
        // at DC and above alike. Stamped as the DC-open case rather than dividing by zero. A part
        // with NO capacitance never takes this arm, however small ω gets.
        bool open = hasC && (omega == 0.0 || cap == 0.0);

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
        // sign on the imaginary part because −1/(jωC) = +j/(ωC), and is DROPPED rather than given an
        // infinite C when the part has none — see RlcElements for why the tidier spelling is NaN.
        double bc   = hasC ? 1.0 / (omega * cap) : 0.0;
        var    diag = new Complex(-r, -omega * l + bc);
        if (diag != Complex.Zero)
            mna.AddBranchConstraint(br, br, diag);
    }
}

/// <inheritdoc cref="SeriesRlcBranchModel"/>
/// <summary>R, L and C in series. Engine reference <c>SRLC</c>.</summary>
public sealed class SeriesRlcModel() : SeriesRlcBranchModel(RlcElements.Rlc), IInductiveBranch
{
    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}

/// <summary>
/// R and L in series — engine reference <c>SRL</c>. A lossy inductor stated as one part: the coil's
/// series resistance is the <c>R</c>, and there is no capacitance to open the branch at DC, so it
/// reads as R at ω = 0 exactly as the physical part does.
/// </summary>
/// <remarks>See <see cref="SeriesRlcBranchModel"/> for the shared stamp and the Mutual contract.</remarks>
public sealed class SeriesRlModel() : SeriesRlcBranchModel(RlcElements.Rl), IInductiveBranch
{
    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}

/// <summary>
/// R and C in series — engine reference <c>SRC</c>. A lossy capacitor, or a series RC damper.
/// </summary>
/// <remarks>
/// <b>The one member of the series family that is not an <see cref="IInductiveBranch"/>.</b> The
/// interface's contract is that the branch it names carries an INDUCTOR current with a −jωL diagonal
/// for a <see cref="MutualInductanceModel"/> to couple to; this branch has no such term, so
/// implementing it would let a Mutual name an SRC and stamp coupling into a diagonal that is not an
/// inductance. Refusing at the reference is the whole point — see
/// <c>MutualInductanceModel.AsInductive</c>, which then names what the part actually is.
/// </remarks>
public sealed class SeriesRcModel() : SeriesRlcBranchModel(RlcElements.Rc);

/// <summary>
/// L and C in series — engine reference <c>SLC</c>. The lossless series-resonant trap: zero
/// impedance at 1/(2π√(LC)), an open at DC.
/// </summary>
/// <remarks>See <see cref="SeriesRlcBranchModel"/> for the shared stamp and the Mutual contract.</remarks>
public sealed class SeriesLcModel() : SeriesRlcBranchModel(RlcElements.Lc), IInductiveBranch
{
    /// <inheritdoc/>
    public int LastBranchIndex => Branch;
}
