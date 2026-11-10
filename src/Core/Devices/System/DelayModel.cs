using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal delay: a matched, reciprocal, lossless two-port whose through path is a pure time
/// delay. Two ports, four nets (<c>[1+, 1−, 2+, 2−]</c>); the single-ended schematic tile ties each
/// port's − net to ground at extraction, as every system block does.
///
/// <code>
///   S11 = S22 = 0
///   S21 = S12 = e^(−jωT)
/// </code>
/// <para>It is an ideal matched delay line stated in TIME rather than in electrical length at a
/// frequency, which is the form a system budget states a delay in: a group delay of exactly
/// <c>T</c> at every frequency, and unit magnitude. At DC it is an ideal through, which the
/// S-block stamp handles because it stamps the definition of S rather than a Z or Y matrix (see
/// <see cref="IdealSBlockModel"/>).</para>
///
/// <para><b>Not the controlled sources' <c>T</c></b>, although the phase factor is the same one. A
/// VCCS or VCVS with a delay is a unilateral dependent source with an ideal sense pair; this is a
/// reciprocal, matched two-port in a 50 Ω (or <c>Z0</c>) system. A block diagram wants this one, a
/// device model wants those.</para>
///
/// <para>A negative <c>T</c> is accepted: non-causal, but in the frequency domain still only a phase
/// slope, and the ordinary way to cancel a known delay elsewhere in a chain.</para>
/// </summary>
public sealed class DelayModel : IdealSBlockModel
{
    private readonly double _delay;

    /// <param name="delaySeconds">The delay, seconds.</param>
    /// <param name="z0">Reference impedance of both ports, ohms; may be complex.</param>
    public DelayModel(double delaySeconds, Complex z0) : base([z0, z0])
    {
        _delay = delaySeconds;
    }

    /// <summary>The delay this block applies, seconds.</summary>
    public double DelaySeconds => _delay;

    /// <summary>Two interchangeable ports, numbered as the attenuator's are.</summary>
    public override string[] TerminalNames => ["1", "2"];

    protected override bool SDependsOnFrequency => _delay != 0.0;

    protected override void FillS(double omega, Complex[,] s)
    {
        double phase = -omega * _delay;
        s[0, 0] = s[1, 1] = Complex.Zero;
        s[0, 1] = s[1, 0] = new Complex(Math.Cos(phase), Math.Sin(phase));
    }
}
