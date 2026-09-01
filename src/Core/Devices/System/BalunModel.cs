using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal balun, in the <b>three-port, ground-referenced</b> form (brief-sys-3 decision D3).
/// Three ports, six nets (<c>[unb+, unb−, bal_p+, bal_p−, bal_n+, bal_n−]</c>); the single-ended
/// tile ties each port's − net to ground at extraction, which is what makes the two balanced ports
/// separate things that can be imbalanced against each other.
///
/// <para><b>Why three ports and not two.</b> An ideal balun IS an ideal transformer, and a
/// transformer is already exactly expressible here as a TWO-port with unequal reference impedances:
/// <c>S = [[0,1],[1,0]]</c> with <c>Z₀₁ = 50</c> and <c>Z₀₂ = 100</c> is an ideal 1:2 balun, exact at
/// every frequency including DC, with no approximation anywhere —
/// <see cref="IdealSBlockModel"/>'s own transformer gate proves that form works. What that form
/// cannot express is <b>imbalance</b>: with no separate balanced ports there is nothing for the two
/// outputs to be imbalanced between. Amplitude and phase imbalance are the first thing a system user
/// asks a balun model for, and a three-port is also what the tile draws, so this is the form that
/// ships. A user who wants the exact ideal transformer instead should place a two-port with unequal
/// port impedances — the user documentation says so, and says why.</para>
///
/// <para><b>Its S-matrix, in full.</b> Port 1 is UNB, port 2 is BAL+, port 3 is BAL−. Write
/// <c>ℓ = 10^(−IL/20)</c> and <c>k = 10^(AmpImb/40)</c>, so that the two balanced outputs sit
/// <c>AmpImb</c> dB apart, split symmetrically about the ideal half-split:</para>
/// <code>
///   S21 = S12 =  (1/√2)·k·ℓ
///   S31 = S13 = −(1/√2)·ℓ/k · e^(−j·PhaseImb)
///   S11 = 0                                        the unbalanced port is matched
///   S22 = S33 = S23 = S32 = 1/2
/// </code>
///
/// <para><b>The <c>1/2</c> block is not a mistake, and it is the interesting part of the model.</b> A
/// lossless, reciprocal three-port CANNOT have all three of its ports matched — that is a theorem,
/// not a modelling shortcut — and a real balun does not isolate its balanced ports from one another
/// either. What the 1/2 block says becomes obvious in the modal basis: with
/// <c>d = (port2 − port3)/√2</c> and <c>c = (port2 + port3)/√2</c>, the ideal matrix above is</para>
/// <code>
///   S(unb, d, c) = [[0, 1, 0],
///                   [1, 0, 0],
///                   [0, 0, 1]]
/// </code>
/// <para>— an <b>ideal through</b> between the unbalanced port and the DIFFERENTIAL mode, and a
/// <b>total reflection</b> for the COMMON mode, which is exactly what a balun is for. The common
/// mode sees an open circuit; the mismatch a user reads at ports 2 and 3 individually is that open,
/// seen one port at a time.</para>
///
/// <para><b>The impedance transformation follows from the same picture.</b> The differential mode's
/// reference impedance is <c>2·Zbal</c> (each balanced port is <c>Zbal</c> to ground), and the
/// unbalanced port's is <c>Zunb</c>, so the block is a lossless ideal transformer of turns ratio
/// <c>n = √(2·Zbal/Zunb)</c>: a differential load <c>R</c> across BAL+/BAL− is seen at the
/// unbalanced port as <c>R·Zunb/(2·Zbal)</c>. At the defaults (<c>Zunb</c> = <c>Zbal</c> = 50 Ω)
/// that is the ordinary 1:2 balun — 100 Ω differential presents 50 Ω single-ended.</para>
///
/// <para><b>No passive-intermod, by decision.</b> The series' PIM overlay does not come to the balun
/// (SYS-3 D3); a user who wants passive intermod in a balanced path places a 0 dB attenuator with a
/// PIM spec in front of it, which is the standalone generator that arrangement exists for.</para>
/// </summary>
public sealed class BalunModel : IdealSBlockModel
{
    private readonly Complex[,] _sFlat;

    /// <param name="zUnb">Unbalanced-port reference impedance, ohms.</param>
    /// <param name="zBal">
    /// Reference impedance of EACH balanced port to ground, ohms — so the differential impedance is
    /// <c>2·zBal</c>.
    /// </param>
    /// <param name="ilDb">Insertion loss, dB. A loss, so it is never snapped.</param>
    /// <param name="ampImbDb">Amplitude imbalance between the two balanced outputs, dB.</param>
    /// <param name="phaseImbRad">
    /// Departure from 180°, in <b>RADIANS</b> — the Elaborator has already applied the parameter's
    /// angle unit, so an authored <c>PhaseImb=6 deg</c> arrives here as 0.1047. Same convention as
    /// <c>TLineModel</c>'s <c>E</c> and the Coupler's <c>Phase</c>.
    /// </param>
    public BalunModel(Complex zUnb, Complex zBal, double ilDb, double ampImbDb, double phaseImbRad)
        : base([zUnb, zBal, zBal])
    {
        double loss = AmplitudeFromDb(ilDb);
        double k    = Math.Pow(10.0, ampImbDb / 40.0);
        double half = 1.0 / Math.Sqrt(2.0);

        // The 180° lives in the SIGN, not in the exponent. Both spellings are the same number, but
        // e^(−jπ) evaluated in floating point carries a 1.2e−16 imaginary residue, and "exactly
        // antiphase at zero imbalance" is a property the gate checks and a user reads off a plot.
        Complex plus  = new(half * k * loss, 0);
        Complex minus = -Complex.FromPolarCoordinates(half / k * loss, -phaseImbRad);

        _sFlat = new Complex[3, 3];
        _sFlat[1, 0] = _sFlat[0, 1] = plus;
        _sFlat[2, 0] = _sFlat[0, 2] = minus;
        _sFlat[1, 1] = _sFlat[2, 2] = _sFlat[1, 2] = _sFlat[2, 1] = new Complex(0.5, 0);
    }

    /// <summary>
    /// The ports' own words. <c>bal+</c>/<c>bal−</c> rather than <c>2</c>/<c>3</c>, because which of
    /// the two carries the inversion is the one thing a reader needs from a branch-current key here.
    /// </summary>
    public override string[] TerminalNames => ["unb", "bal+", "bal-"];

    protected override void FillS(double omega, Complex[,] s)
        => Array.Copy(_sFlat, s, _sFlat.Length);
}
