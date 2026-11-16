using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal coupled transmission-line pair — 4-port, ground-referenced, described by its even- and
/// odd-mode impedances (linear engine, nodal-admittance stamp, no branch currents).
///
/// Nodes (4 signal terminals, common reference = ground "0"):
///   Nodes[0] = port 1, line A near end      Nodes[1] = port 2, line A far end
///   Nodes[2] = port 3, line B near end      Nodes[3] = port 4, line B far end
/// so port 3 sits beside port 1 and port 4 beside port 2. Driving port 1 of a backward-wave
/// (TEM) coupler, port 2 is the through port, port 3 the coupled port and port 4 the isolated port.
///
/// Parameters (units already applied by the elaborator):
///   Ze = even-mode characteristic impedance (Ω).   Zo = odd-mode characteristic impedance (Ω).
///   E  = even-mode electrical length at F (RADIANS — "E=90 deg" arrives as π/2).
///   F  = reference frequency (Hz).
///   Eo = odd-mode electrical length at F (RADIANS, OPTIONAL). Absent → E: the homogeneous (pure
///        TEM, stripline) case, in which both modes travel at one velocity. A different Eo models
///        the even/odd velocity mismatch of an inhomogeneous medium such as microstrip.
///
/// Each mode is an ideal uniform line of its own impedance and length. With the pair's symmetry,
/// the 4×4 admittance is assembled from the two modal 2-port admittances Yᵉ and Yᵒ
/// (<see cref="TLineModel.StampUniformLine"/>'s form, Y11 = coth(γl)/Z, Y12 = −1/(Z·sinh γl)):
///   Y[A_i, A_j] = Y[B_i, B_j] = (Yᵉ_ij + Yᵒ_ij) / 2
///   Y[A_i, B_j] = Y[B_i, A_j] = (Yᵉ_ij − Yᵒ_ij) / 2          (i, j ∈ {near, far})
/// For Ze·Zo = Z0² and equal modal lengths this is the textbook coupler: |S11| = |S41| = 0 at every
/// frequency and the coupled voltage C = (Ze − Zo)/(Ze + Zo) at the quarter-wave frequency.
///
/// Resonance guard: θ = kπ makes sinh(jθ) vanish; the modal admittances are clamped exactly as
/// <see cref="TLineModel.StampUniformLine"/> clamps a single line (same floor), and DC (θ = 0) is
/// the same degeneracy.
/// </summary>
public sealed class CLineModel : ComponentModel, IDcShortedTerminals
{
    /// <summary>Lossless at DC: each line of the pair a short end to end (line A 1-2, line B 3-4), the coupling
    /// (an inductance and a capacitance) vanishing there.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; } = [(0, 1), (2, 3)];

    public override int       PortCount => 4;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly double _ze, _zo;        // even/odd-mode impedance (Ω)
    private readonly double _eRad, _eoRad;   // even/odd electrical length at F (rad)
    private readonly double _fRefHz;         // reference frequency (Hz)

    private const double SinhFloor = 1e-9;

    public CLineModel(double zeOhms, double zoOhms, double evenLengthRad, double oddLengthRad, double refFreqHz)
    {
        Validate(zeOhms, zoOhms, evenLengthRad, oddLengthRad, refFreqHz);
        _ze = zeOhms; _zo = zoOhms; _eRad = evenLengthRad; _eoRad = oddLengthRad; _fRefHz = refFreqHz;
    }

    /// <summary>Throws the refusal <see cref="Refusal"/> reports, if any.</summary>
    public static void Validate(double ze, double zo, double eRad, double eoRad, double fRefHz)
    {
        if (Refusal(ze, zo, eRad, eoRad, fRefHz) is { } why)
            throw new ArgumentException(why.Render());
    }

    /// <summary>The coded refusal for parameters no coupled pair can have; null when they are valid.
    /// Ze below Zo is refused rather than swapped: it is a coupling of the opposite sign, which no
    /// pair of conductors over a common ground produces, and a silent swap would hide a typo.</summary>
    public static Diagnostic? Refusal(double ze, double zo, double eRad, double eoRad, double fRefHz)
    {
        if (!(zo > 0.0))
            return Diagnostic.Create("clin.zo.not-positive", DiagnosticSeverity.Error,
                "CLIN: Zo={zo} ohm must be positive.", ("zo", zo));
        if (!(ze >= zo))
            return Diagnostic.Create("clin.ze.below-zo", DiagnosticSeverity.Error,
                "CLIN: Ze={ze} ohm is below Zo={zo} ohm. The even-mode impedance of a coupled pair is never " +
                "below its odd-mode impedance; check that the two were not swapped.", ("ze", ze), ("zo", zo));
        if (!(eRad >= 0.0) || !(eoRad >= 0.0))
            return Diagnostic.Create("clin.length.negative", DiagnosticSeverity.Error,
                "CLIN: electrical lengths E={e} rad and Eo={eo} rad must not be negative.", ("e", eRad), ("eo", eoRad));
        if (!(fRefHz > 0.0))
            return Diagnostic.Create("clin.f.not-positive", DiagnosticSeverity.Error,
                "CLIN: reference frequency F={f} Hz must be positive.", ("f", fRefHz));
        return null;
    }

    /// <summary>Coupling coefficient (Ze − Zo)/(Ze + Zo): the coupled-port voltage at a quarter wave.</summary>
    public double CouplingCoefficient => (_ze - _zo) / (_ze + _zo);

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double ratio = omega / (2.0 * Math.PI) / _fRefHz;
        StampModalPair(mna, c.Nodes[0], c.Nodes[1], c.Nodes[2], c.Nodes[3],
                       _ze, new Complex(0.0, _eRad * ratio), _zo, new Complex(0.0, _eoRad * ratio));
    }

    /// <summary>
    /// The symmetric coupled pair's 4×4 nodal admittance, assembled from the even and odd modes, each
    /// a uniform line of its own (real) impedance and its own γl = αl + jβl over the pair's length.
    /// Shared by every coupled-line part — CLIN (lossless, γl = jθ), the physical MCLIN, BCLIN and
    /// MBCLIN (dispersive, lossy) — so the even/odd recombination exists once. Node order is the
    /// coupled-pair contract: A near, A far, B near (beside A near), B far.
    /// </summary>
    public static void StampModalPair(IMnaContext mna, int a1, int a2, int b1, int b2,
                                      double zEven, Complex gammaLengthEven, double zOdd, Complex gammaLengthOdd)
    {
        var (ed, eo) = Modal(zEven, gammaLengthEven);
        var (od, oo) = Modal(zOdd, gammaLengthOdd);
        Complex sd = (ed + od) / 2.0, so = (eo + oo) / 2.0;     // same line
        Complex md = (ed - od) / 2.0, mo = (eo - oo) / 2.0;     // across the pair
        int[] n = [a1, a2, b1, b2];
        // Rows/cols in the order a1, a2, b1, b2: [[sd so md mo] [so sd mo md] [md mo sd so] [mo md so sd]].
        Complex[,] y =
        {
            { sd, so, md, mo },
            { so, sd, mo, md },
            { md, mo, sd, so },
            { mo, md, so, sd },
        };
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                mna.AddBlockAdmittance(n[i], n[j], y[i, j]);
    }

    /// <summary>One mode's 2-port admittance (diagonal, off-diagonal) for a line of impedance
    /// <paramref name="z"/> and propagation <paramref name="gl"/> = γl over its length. Shared with the N-line
    /// model (<see cref="MicrostripMultiLineModel"/>), whose modes carry a complex impedance.</summary>
    internal static (Complex Diag, Complex Off) Modal(Complex z, Complex gl)
    {
        Complex sh = Complex.Sinh(gl), ch = Complex.Cosh(gl);
        if (sh.Magnitude < SinhFloor)
            sh = (sh.Magnitude > 0.0 ? sh / sh.Magnitude : Complex.ImaginaryOne) * SinhFloor;
        return (ch / (z * sh), -Complex.One / (z * sh));
    }
}
