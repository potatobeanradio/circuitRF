using System.Numerics;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal filter: a two-port whose S is the RATIONAL response of a lowpass prototype, placed on
/// the frequency axis by a lowpass, highpass or bandpass transformation. Two ports, four nets
/// (<c>[1+, 1−, 2+, 2−]</c>); the single-ended schematic tile ties each port's − net to ground at
/// extraction.
///
/// <para><b>It is a transfer function, not a ladder — deliberately.</b> Synthesising a
/// doubly-terminated LC ladder from prototype g-values and stamping the elements, as
/// <see cref="MatchModel"/> does, cannot take an arbitrary source/load impedance ratio: the
/// termination ratio is fixed by the family and the order (an even-order Chebyshev has a particular
/// one, Butterworth needs equal ends), so <c>Zin</c> and <c>Zout</c> would become a constrained pair
/// with refusals attached. Stamped as an S-matrix through <see cref="IdealSBlockModel"/> the
/// reference impedances are simply what S is DEFINED against: port 1 is matched to <c>Zin</c>, port
/// 2 to <c>Zout</c>, the response is exactly the prototype's, any pair of impedances works, and
/// there is no synthesis feasibility question at all.</para>
///
/// <para><b>So it is also a lossless impedance transformer</b>, which is a real thing a real filter
/// can be designed to be. Said here and in the user documentation rather than left as a surprise:
/// a <c>Zin = 50</c>, <c>Zout = 25</c> filter is matched at BOTH its ports in its passband, and
/// measured in a uniform 50 Ω system it shows the mismatch of a 2:1 transformer, not a fault.</para>
///
/// <para><b>Either impedance may be COMPLEX</b>, which is a filter designed to work between reactive
/// terminations rather than resistive ones — the ordinary case at the ports of a real device.
/// <c>Zin</c> is the impedance port 1 PRESENTS, so <c>Zin = 5 + j100</c> is conjugate-matched —
/// maximum power transfer — by a <c>Term</c> at <c>Z = 5 − j100</c>. The stamp works in the
/// reference impedance, which is the conjugate of these; the constructor below is where that one
/// conversion happens.</para>
///
/// <para><b>The S is the true rational one, not a magnitude.</b> A magnitude-only response is
/// zero-phase, has no group delay, and would make the Bessel option meaningless — Bessel exists for
/// its phase. See <see cref="FilterPrototype"/> for the polynomials and
/// <see cref="FilterNetwork"/> for the transformations.</para>
///
/// <para><b>A parameter the selected response does not read is IGNORED, not refused</b>: a user
/// switching Chebyshev to Butterworth should not have to clear a ripple field, and a user switching
/// bandpass to lowpass should not have to clear the band edges. The parameter descriptions say
/// which family and which form read which.</para>
///
/// <para><b>No passive intermod, and refused by name.</b> A nonlinearity in this repository is
/// memoryless in the port voltages and this block's whole purpose is frequency dependence; the two
/// cannot live in one component. <c>ComponentModelFactory.RefusePimWhereItCannotLive</c> says so and
/// names the alternative — an <c>Atten</c> at a small loss carrying the PIM specification, placed in
/// the signal path.</para>
/// </summary>
public sealed class FilterModel : IdealSBlockModel
{
    private readonly FilterNetwork _network;

    /// <param name="response">The prototype family.</param>
    /// <param name="form">Lowpass, highpass or bandpass.</param>
    /// <param name="order">PROTOTYPE order — a bandpass network is twice this degree.</param>
    /// <param name="fcHz">Cutoff, Hz; read by lowpass and highpass.</param>
    /// <param name="f1Hz">Lower band edge, Hz; read by bandpass.</param>
    /// <param name="f2Hz">Upper band edge, Hz; read by bandpass.</param>
    /// <param name="rippleDb">Passband ripple, dB; read by Chebyshev and elliptic.</param>
    /// <param name="astopDb">Stopband floor, dB; read by inverse Chebyshev and elliptic.</param>
    /// <param name="zIn">The impedance port 1 PRESENTS, ohms; may be complex.</param>
    /// <param name="zOut">The impedance port 2 PRESENTS, ohms; may be complex.</param>
    /// <param name="ilDb">A flat insertion loss laid on top of the ideal response, dB.</param>
    public FilterModel(FilterResponse response, NetworkForm form, int order,
                       double fcHz, double f1Hz, double f2Hz,
                       double rippleDb, double astopDb,
                       Complex zIn, Complex zOut, double ilDb)
        // CONJUGATED: Zin/Zout name what each port PRESENTS, and Kurokawa's S_pp = 0 is
        // Z_seen = conj(Z0), so the reference the stamp works in is their conjugate. This is the
        // whole of that conversion — see IdealSBlockModel's remarks for the rule and why the name
        // decides it. A real pair passes through untouched, being its own conjugate.
        : base([Complex.Conjugate(zIn), Complex.Conjugate(zOut)])
        => _network = FilterNetwork.Create(response, form, order, fcHz, f1Hz, f2Hz,
                                           rippleDb, astopDb, ilDb);

    /// <summary>The whole electrical answer, for a test or a plot that wants it without a solve.</summary>
    public FilterNetwork Network => _network;

    /// <inheritdoc/>
    protected override bool PortParameterIsPresentedImpedance => true;

    /// <summary>Two ports, numbered — a filter is reciprocal and its two ends are interchangeable.</summary>
    public override string[] TerminalNames => ["1", "2"];

    protected override void FillS(double omega, Complex[,] s)
    {
        var (s11, s21, s22) = _network.At(omega);
        s[0, 0] = s11;
        s[0, 1] = s21;      // reciprocal: S12 = S21
        s[1, 0] = s21;
        s[1, 1] = s22;
    }
}
