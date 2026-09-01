using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal attenuator: a matched, reciprocal, frequency-flat two-port that knocks the signal down
/// by a stated number of dB. Two ports, four nets (<c>[1+, 1−, 2+, 2−]</c>); the single-ended
/// schematic tile ties each port's − net to ground at extraction.
///
/// <para><b>Its S-matrix, in full:</b></para>
/// <code>
///   S11 = S22 = ρ = 10^(−RL/20)      (0 exactly when RL ≥ 150 dB — the default 200 means MATCHED)
///   S21 = S12 = 10^(−Loss/20)        (taken literally at every value, including 0 dB)
/// </code>
/// <para>Both off-diagonal entries are the same number because an attenuator is reciprocal and
/// symmetric — nothing in it distinguishes its two ports, which is why the tile draws them
/// unlabelled and interchangeable.</para>
///
/// <para><b><c>Loss = 0</c> is a legitimate part to place.</b> With the default return loss it is an
/// ideal through — <c>S = [[0,1],[1,0]]</c>, which has no Z matrix — and the stamp handles it
/// because it stamps the definition of S rather than a transformation of it (see
/// <see cref="IdealSBlockModel"/>).</para>
///
/// <para><b>The standalone passive-intermod generator is this block with a SMALL loss, and not with
/// <c>Loss = 0</c>.</b> brief-sys-4 asks for the latter, and it cannot exist: a perfectly matched
/// 0 dB attenuator is a wire, a wire has no Y matrix, and a component with no Y cannot be written
/// as the memoryless <c>i = f(v)</c> that every nonlinearity in this repository is. That is a
/// theorem about the object, not a limit of the implementation — <c>det(I + S) = 0</c> exactly —
/// and it is refused BY NAME at construction rather than producing a NaN inside a Newton iteration.
/// <c>Loss = 0.01 dB</c> is 0.1% of amplitude, is invisible on any plot, inverts perfectly well, and
/// is what the user documentation should tell people to place in front of a filter or a duplexer.
/// A finite return loss also lifts the degeneracy, for the same reason.</para>
///
/// <para><b>Loss does not snap and return loss does.</b> A loss is what the component is FOR, so
/// 200 dB of it is a 200 dB pad and is stamped as one. A return loss is a non-ideality, so 200 dB of
/// it means the reflection is not there — no entry in the matrix rather than 1e-10 of one. No
/// passivity check sits between the two: a user is allowed to type numbers that do not describe a
/// passive part, and this model refuses only what cannot be stamped.</para>
/// </summary>
public sealed class AttenuatorModel : IdealSBlockModel
{
    private readonly Complex _thru;
    private readonly Complex _refl;

    /// <param name="lossDb">Insertion loss, a positive number of dB.</param>
    /// <param name="z0">Reference impedance of both ports, ohms; may be complex.</param>
    /// <param name="returnLossDb">Return loss of both ports; ≥ 150 dB means exactly matched.</param>
    /// <param name="pimDbm">
    /// Third-order passive-intermod product level at port 2, dBm, with two carriers of
    /// <paramref name="pimPcDbm"/> each entering port 1. At or below −150 dBm the block is exactly
    /// linear and no overlay is built at all.
    /// </param>
    /// <param name="pimPcDbm">Power per carrier the level above was stated at, dBm.</param>
    public AttenuatorModel(double lossDb, Complex z0, double returnLossDb,
                           double pimDbm = -200.0, double pimPcDbm = 43.0)
        : base([z0, z0])
    {
        _thru = new Complex(AmplitudeFromDb(lossDb), 0);
        _refl = new Complex(SuppressedAmplitude(returnLossDb), 0);

        // Carriers into port 1, product read at port 2 — the forward measurement, and the only pair
        // of ports a two-port has. Last statement of the constructor: the overlay is derived from
        // this block's own S, which FillS cannot answer for until the two fields above are set.
        EnablePim(pimDbm, pimPcDbm, inPort: 0, outPort: 1);
    }

    /// <summary>Two interchangeable ports, so they carry numbers — which is what the glyph draws.</summary>
    public override string[] TerminalNames => ["1", "2"];

    protected override void FillS(double omega, Complex[,] s)
    {
        s[0, 0] = s[1, 1] = _refl;
        s[0, 1] = s[1, 0] = _thru;
    }
}
