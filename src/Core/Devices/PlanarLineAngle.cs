using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A planar line's electrical length and its physical length, each from the other, at one frequency:
/// θ = 2π·F·L·√εeff(F)/c₀ (designer feedback round 21 — MLIN, CPWG and SLIN take E at F in place of L,
/// as TLIN and MKLOPF already take an alternative to their length).
///
/// <para>εeff is the line MODEL's own at F — the dispersive value a run stamps — and does not depend on
/// the length, so any instance of the line at the right width and substrate answers it.</para>
/// </summary>
public static class PlanarLineAngle
{
    /// <summary>The length, in metres, whose phase at <paramref name="fHz"/> is <paramref name="eRad"/>.</summary>
    public static double LengthMeters(IPlanarLineModel line, double eRad, double fHz)
    {
        ArgumentNullException.ThrowIfNull(line);
        return LengthMeters(line.LineParameters(fHz).Eeff, eRad, fHz);
    }

    /// <summary>The phase, in radians, of <paramref name="lengthMeters"/> of the line at <paramref name="fHz"/>.</summary>
    public static double AngleRad(IPlanarLineModel line, double lengthMeters, double fHz)
    {
        ArgumentNullException.ThrowIfNull(line);
        return AngleRad(line.LineParameters(fHz).Eeff, lengthMeters, fHz);
    }

    /// <summary><see cref="LengthMeters(IPlanarLineModel, double, double)"/> for an εeff already read at F.</summary>
    public static double LengthMeters(double eeff, double eRad, double fHz)
        => eRad * MicrostripLoss.SpeedOfLight / (2.0 * Math.PI * fHz * Math.Sqrt(eeff));

    /// <summary><see cref="AngleRad(IPlanarLineModel, double, double)"/> for an εeff already read at F.</summary>
    public static double AngleRad(double eeff, double lengthMeters, double fHz)
        => 2.0 * Math.PI * fHz * lengthMeters * Math.Sqrt(eeff) / MicrostripLoss.SpeedOfLight;

    /// <summary>
    /// The length whose phase at the frequency <paramref name="line"/> was read at is <paramref name="eRad"/>, by the phase
    /// constant MLIN stamps there (<see cref="MicrostripLineParameters.PhaseRadPerM"/>): the lossless β of its dispersive
    /// εeff and the causal phase its loss comes with (<see cref="Planar.CausalLoss"/>). What a planned passive that
    /// promises θ at F sizes its lines by, so the line it draws is θ in the run that simulates it.
    /// </summary>
    public static double LengthMeters(in MicrostripLineParameters line, double eRad) => eRad / line.PhaseRadPerM;

    /// <summary>The phase of <paramref name="lengthMeters"/> of the line at the frequency <paramref name="line"/> was read
    /// at, by the phase constant MLIN stamps (<see cref="LengthMeters(in MicrostripLineParameters, double)"/>).</summary>
    public static double AngleRad(in MicrostripLineParameters line, double lengthMeters) => line.PhaseRadPerM * lengthMeters;
}
