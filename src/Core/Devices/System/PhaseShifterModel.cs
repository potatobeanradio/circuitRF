using System.Numerics;
using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Core.Devices;

/// <summary>
/// brief-paa-4 R-paa4-7 — the ideal phase shifter: a matched, reciprocal two-port that turns the signal's phase by a stated angle,
/// rounded to its bits, with an insertion loss and an optional true time delay (docs/design/phased-array.md §4.5). Two ports, four
/// nets (<c>[1+, 1−, 2+, 2−]</c>), as every block of the System family.
///
/// <code>
///   S11 = S22 = 0
///   S21 = S12 = 10^(−IL/20) · e^{−jφ_q} · e^{−jω·Delay},   φ_q = φ rounded to 360°/2^Bits (Bits = 0: φ itself)
/// </code>
/// <para><b>Phase is the phase it DELAYS the signal by</b>, as a line's electrical length is: <c>Phase=90</c> is <c>S21 = −j</c>. An
/// array's weight is the opposite sign by definition (<c>w = A·e^{jφ}</c>), so a channel cell that steers through this block writes
/// <c>Phase=-steer(…)</c>.</para>
/// <para><b>A flat phase, which nothing else in the library is.</b> A line's phase is a delay and grows with frequency; the Chain Budget
/// made a flat phase from one path of a 2-way Combiner. This is the block that stands for itself, for a channel cell or an Elements-mode
/// bench. <c>Phase</c> is degrees, as the Combiner's <c>Phase[k]</c> is (the elaborator resolves it to radians, so <c>Phase=90</c> and
/// <c>Phase=90 deg</c> are one angle); <c>Delay</c> is seconds and is not quantised.</para>
///
/// <para><b>Lossless at IL = 0, and its noise is its loss</b>: passive, so its thermal noise is its S, as every passive System block's
/// is (<see cref="IdealSBlockModel"/>).</para>
/// </summary>
public sealed class PhaseShifterModel : IdealSBlockModel
{
    public const string TypeName = "PhaseShifter";

    private readonly Complex _thru;
    private readonly double _delay;

    /// <param name="phaseRad">The phase, radians, before quantisation.</param>
    /// <param name="ilDb">Insertion loss, dB.</param>
    /// <param name="bits">Phase bits; 0 is continuous.</param>
    /// <param name="delayS">True time delay, seconds.</param>
    /// <param name="z0">Reference impedance of both ports.</param>
    public PhaseShifterModel(double phaseRad, double ilDb, int bits, double delayS, Complex z0) : base([z0, z0])
    {
        QuantisedPhaseRad = ArrayWeights.QuantisePhase(phaseRad, bits);
        _thru = Complex.FromPolarCoordinates(AmplitudeFromDb(ilDb), -QuantisedPhaseRad);
        _delay = delayS;
    }

    /// <summary>The phase the block applies, radians, after its bits.</summary>
    public double QuantisedPhaseRad { get; }

    public override string[] TerminalNames => ["1", "2"];

    protected override bool SDependsOnFrequency => _delay != 0;

    protected override void FillS(double omega, Complex[,] s)
    {
        var t = _delay == 0 ? _thru : _thru * Complex.FromPolarCoordinates(1, -omega * _delay);
        s[0, 1] = s[1, 0] = t;
    }
}
