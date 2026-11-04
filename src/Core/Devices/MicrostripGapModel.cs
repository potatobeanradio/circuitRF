using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MGAP — the end-to-end gap between two microstrip lines of width <c>W</c>, <c>S</c> apart (brief-sdl-9).
/// Two-port: pin 1 is the end of the line on one side, pin 2 the end of the line on the other, and the
/// reference planes are the two edges of the gap. The circuit is the symmetric pi of
/// <see cref="MicrostripGap"/>: <c>Cg</c> across the gap and <c>Cp</c> from each end to ground.
///
/// <para><b><c>S ≤ 0</c> is refused</b>, not evaluated: the two lines would touch or overlap, which is a
/// joint, not a gap, and nothing in the closed form describes it. <c>W ≤ 0</c> is refused for the same
/// reason. Outside the closed form's stated range the instance warns, once per ratio, as every other
/// microstrip discontinuity does.</para>
/// </summary>
public sealed class MicrostripGapModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly MicrostripValidityReporter _reporter;

    /// <summary>The series capacitance across the gap, in farads.</summary>
    public double SeriesCapacitance { get; }

    /// <summary>The shunt capacitance from each line end to ground, in farads.</summary>
    public double ShuntCapacitance { get; }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripGapModel(double wMeters, double sMeters, double hMeters, double epsR, string instancePath)
    {
        if (MicrostripGap.Refusal(wMeters, sMeters, hMeters) is { } why)
            throw new ParameterRefusalException(why.Render());

        _reporter = new MicrostripValidityReporter(instancePath);
        (SeriesCapacitance, ShuntCapacitance) = MicrostripGap.Capacitances(wMeters, sMeters, hMeters, epsR, _reporter);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        // At DC every capacitance is open: the gap is a break, and each side is held by its own line.
        if (omega <= 0.0) return;

        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        mna.AddAdmittance(n1, n2, new Complex(0.0, omega * SeriesCapacitance));
        mna.AddAdmittance(n1, 0, new Complex(0.0, omega * ShuntCapacitance));
        mna.AddAdmittance(n2, 0, new Complex(0.0, omega * ShuntCapacitance));
    }
}
