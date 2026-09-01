using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Systems;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal duplexer: two <see cref="FilterModel"/> responses sharing one antenna node. Three ports
/// (ANT, TX, RX), six nets (<c>[ant+, ant−, tx+, tx−, rx+, rx−]</c>); the single-ended tile ties each
/// port's − net to ground at extraction.
///
/// <para><b>No new mathematics at all, and that is the design.</b> It stamps two independent
/// two-port wave constraints — one between the ANT pair and the TX pair, one between the ANT pair
/// and the RX pair — onto the same ANT nets. Four branch currents, no internal node. The
/// antenna-node interaction, the TX-to-RX isolation and each arm's out-of-band reflection are then
/// CONSEQUENCES of the shared node rather than parameters; there is no 3×3 matrix to write down,
/// because a junction is a network operation and not a combination of two scattering matrices.</para>
///
/// <para><b>There is deliberately no <c>Isolation</c> parameter.</b> The isolation a duplexer
/// achieves is what its two responses and their junction produce, and a user who typed one would be
/// overriding physics with a number. If the measured isolation surprises them, that is the model
/// telling them something true about their band plan.</para>
///
/// <para><b>And no phasing line.</b> A real duplexer needs one because real filters do not present
/// an ideal open outside their own band; the rational reflections stamped here DO carry the right
/// phase, so the ideal junction behaves. A user who wants to model the phasing places a
/// <see cref="TLineModel"/> in the arm — which is a component they can see, tune and sweep, rather
/// than a hidden length inside this one.</para>
///
/// <para><b>Why it is not an <see cref="IdealSBlockModel"/> subclass.</b> That class is "one
/// component, one S-matrix", and this component is two. It reuses the family's stamp directly
/// (<see cref="IdealSBlockModel.StampWaveConstraints"/>) rather than pretending to have a matrix it
/// does not have — the same argument, one level down, that made the wave constraint the right stamp
/// in the first place.</para>
///
/// <para><b>No passive intermod</b>, refused by name at the factory for the same reason the filter
/// is: a memoryless nonlinearity cannot attach to a rational transfer function inside one
/// component.</para>
/// </summary>
public sealed class DuplexerModel : ComponentModel
{
    /// <summary>Ports of the ANT-to-TX arm, as indices into this component's own port list.</summary>
    private static readonly int[] TxArmPorts = [0, 1];

    /// <summary>Ports of the ANT-to-RX arm.</summary>
    private static readonly int[] RxArmPorts = [0, 2];

    private readonly FilterNetwork _tx, _rx;
    private readonly Complex[] _txZ0, _rxZ0;
    private readonly Complex[,] _s = new Complex[2, 2];

    /// <param name="tx">The ANT-to-TX filter.</param>
    /// <param name="rx">The ANT-to-RX filter.</param>
    /// <param name="zAnt">The impedance the shared antenna port PRESENTS, ohms; may be complex.</param>
    /// <param name="zTx">The impedance the TX port PRESENTS, ohms; may be complex.</param>
    /// <param name="zRx">The impedance the RX port PRESENTS, ohms; may be complex.</param>
    public DuplexerModel(FilterNetwork tx, FilterNetwork rx, Complex zAnt, Complex zTx, Complex zRx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(rx);

        _tx = tx;
        _rx = rx;

        // Two rules from IdealSBlockModel, kept by hand because a duplexer is not one of them: a
        // reference with no positive resistive part is not a port (rule 2, else a NaN square root
        // surfaces as a non-convergence with nothing attached), and these three parameters name what
        // each port PRESENTS — they are the arms' own Zin/Zout under a shorter spelling, and the
        // registry documents them as exactly that — so the reference the stamp works in is their
        // CONJUGATE. A real value is unchanged by either.
        Complex Ok(Complex z) => Complex.Conjugate(z.Real > 0 ? z : new Complex(50.0, 0));
        Complex a = Ok(zAnt);
        _txZ0 = [a, Ok(zTx)];
        _rxZ0 = [a, Ok(zRx)];
    }

    /// <inheritdoc/>
    public override int PortCount => 3;

    /// <inheritdoc/>
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>Named, not numbered: a duplexer's three ports are not interchangeable.</summary>
    public override string[] TerminalNames => ["ANT", "TX", "RX"];

    /// <summary>The ANT-to-TX arm's response.</summary>
    public FilterNetwork Tx => _tx;

    /// <summary>The ANT-to-RX arm's response.</summary>
    public FilterNetwork Rx => _rx;

    /// <summary>Branch indices of the TX arm's two ports, set during each <see cref="Stamp"/>.</summary>
    public int[] TxBranchIndices { get; } = [-1, -1];

    /// <summary>Branch indices of the RX arm's two ports.</summary>
    public int[] RxBranchIndices { get; } = [-1, -1];

    /// <summary>
    /// The two-port S of one arm at <paramref name="omega"/>, for a test that wants to compare an
    /// arm against the standalone filter without going through a solve. Buffer is the model's own
    /// and is overwritten by the next call.
    /// </summary>
    /// <param name="arm">The arm's response — <see cref="Tx"/> or <see cref="Rx"/>.</param>
    /// <param name="omega">Angular frequency; the sign rule below is applied here.</param>
    public Complex[,] ArmSAt(FilterNetwork arm, double omega)
    {
        ArgumentNullException.ThrowIfNull(arm);

        // S(−ω) = conj(S(ω)), keyed on the sign of the ω handed in — the rule IdealSBlockModel owns
        // for its own subclasses, repeated here because this component is not one of them. A
        // rational response is genuinely complex, so unlike the flat blocks it would notice.
        var (s11, s21, s22) = arm.At(Math.Abs(omega));
        if (omega < 0.0) { s11 = Complex.Conjugate(s11); s21 = Complex.Conjugate(s21); s22 = Complex.Conjugate(s22); }

        _s[0, 0] = s11;
        _s[0, 1] = s21;         // reciprocal
        _s[1, 0] = s21;
        _s[1, 1] = s22;
        return _s;
    }

    /// <inheritdoc/>
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        // Two stamps, in this order, onto ONE node list. The ANT pair appears in both, which is the
        // shared node — and is why the two arms are not independent even though their matrices are.
        IdealSBlockModel.StampWaveConstraints(
            mna, c.Nodes, ArmSAt(_tx, omega), _txZ0, TxBranchIndices, portNodes: TxArmPorts);
        IdealSBlockModel.StampWaveConstraints(
            mna, c.Nodes, ArmSAt(_rx, omega), _rxZ0, RxBranchIndices, portNodes: RxArmPorts);
    }
}
