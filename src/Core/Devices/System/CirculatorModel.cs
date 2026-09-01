using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>Which way a <see cref="CirculatorModel"/> passes energy around its three ports.</summary>
public enum CirculatorDirection
{
    /// <summary>1 → 2, 2 → 3, 3 → 1.</summary>
    CW,

    /// <summary>1 → 3, 3 → 2, 2 → 1 — the same component turned the other way.</summary>
    CCW,
}

/// <summary>
/// The ideal circulator: a three-port, frequency-flat, and — uniquely in this repository —
/// <b>non-reciprocal</b>. <c>S ≠ Sᵀ</c>, which is the whole point of the component and the reason
/// every other passive here can get away with a symmetric matrix.
///
/// <para>Three ports, six nets (<c>[1+, 1−, 2+, 2−, 3+, 3−]</c>); the single-ended tile ties each
/// port's − net to ground at extraction. Ports are numbered around the circle and
/// <see cref="CirculatorDirection"/> says which way energy goes, so the drawing and the stamp agree
/// by construction.</para>
///
/// <para><b>Its S-matrix, in full.</b> Write <c>ι = 10^(−IL/20)</c> for the forward path,
/// <c>σ = 10^(−Isolation/20)</c> (0 exactly at ≥ 150 dB) for what leaks the wrong way round, and
/// <c>ρ = 10^(−RL/20)</c> (0 exactly at ≥ 150 dB) at each port:</para>
/// <code>
///   CW:   S21 = S32 = S13 = ι      S12 = S23 = S31 = σ      S_ii = ρ, or Γ_i if stated
///   CCW:  the transpose of it — the two sets exchange, and nothing else moves.
/// </code>
/// <para>So the default (0 dB of loss, 200 dB of isolation and return loss, CW) stamps exactly</para>
/// <code>
///   S = [[0, 0, 1],
///        [1, 0, 0],
///        [0, 1, 0]]
/// </code>
///
/// <para><b>This S has no Z matrix, and that is why the repository grew a third N-port stamp.</b>
/// For the ideal matrix above <c>det(I − S) = 0</c> <i>exactly</i> — it is a permutation matrix, so
/// 1 is one of its eigenvalues — and <c>Z = Z₀(I+S)(I−S)⁻¹</c> therefore does not exist at all. No
/// tolerance, no near-singularity, no conditioning argument: the object a Z-based stamp would have
/// to write down is not there. Its <b>Y does</b> exist (<c>det(I + S) = 2</c>) and works out to</para>
/// <code>
///   Y = (1/Z₀)·[[ 0,  1, −1],
///               [−1,  0,  1],
///               [ 1, −1,  0]]
/// </code>
/// <para>— antisymmetric, <b>zero diagonal</b>, and itself singular, because every row and column
/// sums to zero as a floating network's must. SYS-4's memoryless passive-intermod overlay needs that
/// Y; the wave constraint <see cref="IdealSBlockModel"/> stamps needs neither form, which is exactly
/// the property that made it the one worth building. Both facts are asserted by test
/// (<c>CirculatorModelTests</c>), so the next reader who wonders why finds the answer executable
/// rather than only written down here.</para>
///
/// <para><b>The port match can be DETUNED, per port, in magnitude and phase.</b> A real circulator
/// is notoriously badly matched, and what a power amplifier connected to port 1 actually feels is
/// not a return loss but a complex reflection — the same |Γ| at a different angle presents a
/// completely different load. <c>VSWR1</c>/<c>VSWR2</c>/<c>VSWR3</c> with <c>Ang1</c>/<c>Ang2</c>/
/// <c>Ang3</c> set that port's own <c>S_pp</c> directly:</para>
/// <code>
///   S_pp = ((VSWRp − 1)/(VSWRp + 1)) · exp(j·Angp)
/// </code>
/// <para><b>VSWR = 1 means "not stated"</b>, and that port falls back to the isotropic
/// <c>RL</c> — so the datasheet form (one return loss for the whole part) still works and nothing
/// changes for a user who never touches these. It is not a second spelling of <c>RL</c> that can
/// contradict it: <c>RL</c> is the default, a stated <c>VSWRp</c> is that port's own answer, and one
/// is reached only where the other is absent.</para>
///
/// <para><b>Why not a complex <c>Z0</c>, which the block would otherwise accept.</b> Because it does
/// not do this job. <c>Z0</c> is the reference for ALL THREE ports and for what <c>IL</c> and
/// <c>Isolation</c> are stated against, and with the ideal permutation S above the reflection an
/// amplifier at port 1 would then see is the PRODUCT of the port-2 and port-3 terminations'
/// mismatches — a wave leaves port 2, reflects off that term, circulates to port 3, reflects again,
/// and only then arrives back at port 1. With all three ports in <c>Z_L</c> it is exactly</para>
/// <code>
///   Γ₁ = conj(ρ²)      with   ρ = (Z_L − conj(Z0)) / (Z_L + Z0)
/// </code>
/// <para>in the block's own reference frame, and a further reference change on top of that by the
/// time a 50 Ω system measures it. Nothing a user typed, and not monotone in anything they would
/// think to turn. <c>S_pp</c> is the port's own reflection with the other ports matched, which is
/// what a VSWR number means and what the amplifier feels: <c>Z = Z₀(1 + Γ)/(1 − Γ)</c>, exactly.
/// Both statements are measured in <c>CirculatorDetuneSParamTests</c>.</para>
///
/// <para>The detune is frequency-FLAT, which is a deliberate simplification and not an oversight: a
/// real junction's match rotates with frequency, and a fixed angle is the knob a user asked for when
/// they want to see what a stated mismatch does to a PA. It also keeps the block memoryless, so
/// passive intermod still works over it — the quadrature term <see cref="IdealSBlockModel"/> already
/// carries is what lets a complex S host a nonlinearity at all.</para>
///
/// <para><b>Non-reciprocity is not an approximation here.</b> With the isolation off, <c>S12</c> is
/// not small — it is absent, no entry stamped at all — so a simulated <c>S21/S12</c> ratio is
/// infinite rather than 200 dB. That is the same "ideal means the term is ABSENT" standard the rest
/// of the family keeps, and it is what makes an isolator built from a circulator with one port
/// terminated behave exactly as one.</para>
/// </summary>
public sealed class CirculatorModel : IdealSBlockModel
{
    private readonly Complex[,] _sFlat;

    /// <param name="direction">Which way energy goes round the circle.</param>
    /// <param name="ilDb">Loss along the forward path, dB. A loss, so it is never snapped.</param>
    /// <param name="isolationDb">Reverse leakage, dB; ≥ 150 means none.</param>
    /// <param name="returnLossDb">
    /// Return loss at every port that does not state its own <paramref name="vswr"/>; ≥ 150 means
    /// exactly matched.
    /// </param>
    /// <param name="z0">Reference impedance of all three ports, ohms; may be complex.</param>
    /// <param name="vswr">
    /// Per-port voltage standing-wave ratio, three entries. A value at or below 1 means the port did
    /// not state one and falls back to <paramref name="returnLossDb"/>. Null is the same as three 1s.
    /// </param>
    /// <param name="angRad">
    /// Per-port angle of that port's reflection coefficient, RADIANS (the Elaborator applies the
    /// parameter's own angle unit, the same convention the Coupler's <c>Phase</c> carries). Read only
    /// where <paramref name="vswr"/> stated one. Null is three zeros.
    /// </param>
    /// <param name="pimDbm">
    /// Third-order passive-intermod product level, dBm, read at the port the block circulates INTO
    /// (port 2 clockwise, port 3 counter-clockwise) with two carriers of
    /// <paramref name="pimPcDbm"/> each entering port 1. At or below −150 dBm the block is exactly
    /// linear and no overlay is built at all.
    /// </param>
    /// <param name="pimPcDbm">Power per carrier the level above was stated at, dBm.</param>
    public CirculatorModel(CirculatorDirection direction, double ilDb, double isolationDb,
                           double returnLossDb, Complex z0,
                           double pimDbm = -200.0, double pimPcDbm = 43.0,
                           IReadOnlyList<double>? vswr = null, IReadOnlyList<double>? angRad = null)
        : base([z0, z0, z0])
    {
        double fwd  = AmplitudeFromDb(ilDb);
        double rev  = SuppressedAmplitude(isolationDb);
        double refl = SuppressedAmplitude(returnLossDb);

        _sFlat = new Complex[3, 3];
        for (int p = 0; p < 3; p++) _sFlat[p, p] = PortReflection(p, refl, vswr, angRad);

        // CW is 1→2, 2→3, 3→1: the entry that CARRIES a wave from port p is S[(p+1) mod 3, p].
        // CCW is the transpose, and is written as one rather than as a second table, because two
        // tables are two things to keep in step.
        for (int p = 0; p < 3; p++)
        {
            int next = (p + 1) % 3;
            bool cw = direction == CirculatorDirection.CW;
            _sFlat[next, p] = new Complex(cw ? fwd : rev, 0);
            _sFlat[p, next] = new Complex(cw ? rev : fwd, 0);
        }

        // The product level is stated where the FORWARD path lands: port 2 clockwise, port 3
        // counter-clockwise. Reading it at the isolated port instead would state the level of a
        // product that is not supposed to be there, and would divide by an isolation the user has
        // usually left ideal — the overlay refuses a zero path rather than dividing by it.
        EnablePim(pimDbm, pimPcDbm,
                  inPort: 0,
                  outPort: direction == CirculatorDirection.CW ? 1 : 2);
    }

    /// <summary>
    /// A VSWR at or below this is "not stated" and the port falls back to <c>RL</c>. Exactly 1 is
    /// the ideal port and the parameter's own default, so the test is on the ideal value itself
    /// rather than on a tolerance band — a user who types 1.0000001 meant a mismatch and gets one.
    /// </summary>
    private const double VswrUnstated = 1.0;

    /// <summary>
    /// Port <paramref name="port"/>'s own <c>S_pp</c>: the stated VSWR and angle when it has one,
    /// and the isotropic <c>RL</c> amplitude when it does not.
    ///
    /// <para>A VSWR BELOW 1 is not a reflection at all and is read as "not stated" rather than
    /// refused — it is the same reading a missing parameter gets, and the parameter's whole range
    /// starts at 1.</para>
    /// </summary>
    private static Complex PortReflection(int port, double refl,
                                          IReadOnlyList<double>? vswr, IReadOnlyList<double>? angRad)
    {
        double v = vswr is not null && port < vswr.Count ? vswr[port] : VswrUnstated;
        if (!(v > VswrUnstated)) return new Complex(refl, 0);

        double mag = (v - 1.0) / (v + 1.0);
        double ang = angRad is not null && port < angRad.Count ? angRad[port] : 0.0;
        return Complex.FromPolarCoordinates(mag, ang);
    }

    /// <summary>Ports are numbered round the circle, which is how the glyph labels them.</summary>
    public override string[] TerminalNames => ["1", "2", "3"];

    protected override void FillS(double omega, Complex[,] s)
        => Array.Copy(_sFlat, s, _sFlat.Length);
}
