using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MRADIAL — a microstrip radial stub, one terminal over the implicit reference: the shunt admittance of an open
/// sector (<see cref="MicrostripRadialStub"/>), or with <c>Sides</c> = 2 of two sectors back to back (a butterfly),
/// which are two such stubs in parallel. The terminal is the middle of the sector's narrow end, where the feed line of
/// width <c>W</c> meets it. At DC the stub is open and stamps nothing.
///
/// <para>A geometry the closed form cannot describe is refused (<see cref="MicrostripRadialStub.Refusal"/>). The
/// equivalent line's validity findings are MLIN's own, and the instance says once if a run goes past the single-mode
/// limit (an arc of a guided wavelength).</para>
/// </summary>
public sealed class MicrostripRadialStubModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _w, _ro, _angleDeg;
    private readonly int _sides;
    private readonly MicrostripSubstrate _s;
    private readonly MicrostripLineModel _equivalent;
    private readonly MicrostripValidityReporter _reporter;

    public MicrostripRadialStubModel(double wMeters, double roMeters, double angleDeg, int sides, MicrostripSubstrate s,
        string instancePath)
    {
        if (MicrostripRadialStub.Refusal(wMeters, roMeters, angleDeg, sides) is { } why)
            throw new ParameterRefusalException(why);
        (_w, _ro, _angleDeg, _sides, _s) = (wMeters, roMeters, angleDeg, sides, s);
        _equivalent = new MicrostripLineModel(MicrostripRadialStub.EquivalentWidth(wMeters, roMeters, angleDeg), 0.0,
            s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, instancePath, s.RoughnessMeters);
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => [.. _equivalent.DrainWarnings(), .. _reporter.Drain()];

    /// <summary>The admittance from the terminal to the reference at <paramref name="freqHz"/> &gt; 0: one sector's, or
    /// a butterfly's two in parallel.</summary>
    public Complex Admittance(double freqHz)
    {
        Complex z = MicrostripRadialStub.SectorImpedance(freqHz, _w, _ro, _angleDeg, _s, _equivalent, out double arc);
        if (arc > 1.0)
            _reporter.ReportOnce("MRADIAL:single-mode",
                $"MRADIAL's arc is {arc:G3} guided wavelengths long at {freqHz / 1e9:G4} GHz. Past one wavelength a centred "
              + "feed excites the sector's next mode, which the radial-line closed form does not carry; EM is the "
              + "reference there.");
        return _sides / z;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (omega <= 0.0) return;
        mna.AddAdmittance(c.Nodes[0], 0, Admittance(omega / (2.0 * Math.PI)));
    }
}
