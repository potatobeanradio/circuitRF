namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// The open end of a microstrip line: the field fringing past the strip's end, as the length Δl the line would need to
/// run on to hold the same charge (M. Kirschning, R. H. Jansen and N. H. L. Koster, "Accurate model for open end effect
/// of microstrip lines," Electronics Letters 17(3), 123–125, 1981,
/// <a href="https://doi.org/10.1049/el:19810088">10.1049/el:19810088</a>). With u = W/h and the line's static εeff:
/// <code>
///   Δl/h = ξ1·ξ3·ξ5 / ξ4
///   ξ1 = 0.434907 · (εeff^0.81 + 0.26)/(εeff^0.81 − 0.189) · (u^0.8544 + 0.236)/(u^0.8544 + 0.87)
///   ξ2 = 1 + u^0.371 / (2.358·εr + 1)
///   ξ3 = 1 + 0.5274·atan(0.084·u^(1.9413/ξ2)) / εeff^0.9236
///   ξ4 = 1 + 0.0377·atan(0.067·u^1.456)·(6 − 5·exp(0.036·(1 − εr)))
///   ξ5 = 1 − 0.218·exp(−7.5·u)
/// </code>
/// <para>Δl is geometry: it does not move with frequency. The end is stamped as the capacitance that length of line
/// holds, C = Δl·√εeff/(c·Z0), at the line's own dispersive Z0 and εeff at each frequency; as an admittance jωC that is
/// the line run on by Δl to within (βΔl)²/3. Stated range 0.01 ≤ W/h ≤ 100, εr ≤ 128; outside it the same expressions
/// are evaluated and the instance is told once.</para>
/// </summary>
public static class MicrostripOpenEnd
{
    public static readonly ValidityRange WidthOverHeight = new(0.01, 100.0);
    public static readonly ValidityRange Permittivity = new(1.0, 128.0);

    /// <summary>The end extension Δl (m) of a strip <paramref name="wMeters"/> wide on a substrate
    /// <paramref name="hMeters"/> high, with the line's (or the mode's) static εeff.</summary>
    public static double Extension(double wMeters, double hMeters, double epsR, double eeffStatic,
        MicrostripValidityReporter? reporter = null)
    {
        double u = wMeters / hMeters;
        reporter?.CheckRange("open end", "W/h", u, WidthOverHeight.Min, WidthOverHeight.Max);
        reporter?.CheckRange("open end", "Er", epsR, Permittivity.Min, Permittivity.Max);

        double e81 = Math.Pow(eeffStatic, 0.81), u85 = Math.Pow(u, 0.8544);
        double x1 = 0.434907 * (e81 + 0.26) / (e81 - 0.189) * (u85 + 0.236) / (u85 + 0.87);
        double x2 = 1.0 + Math.Pow(u, 0.371) / (2.358 * epsR + 1.0);
        double x3 = 1.0 + 0.5274 * Math.Atan(0.084 * Math.Pow(u, 1.9413 / x2)) / Math.Pow(eeffStatic, 0.9236);
        double x4 = 1.0 + 0.0377 * Math.Atan(0.067 * Math.Pow(u, 1.456)) * (6.0 - 5.0 * Math.Exp(0.036 * (1.0 - epsR)));
        double x5 = 1.0 - 0.218 * Math.Exp(-7.5 * u);
        return hMeters * x1 * x3 * x5 / x4;
    }

    /// <summary>The capacitance (F) an extension of <paramref name="extensionMeters"/> holds on a line of
    /// <paramref name="eeff"/> and <paramref name="z0"/>.</summary>
    public static double Capacitance(double extensionMeters, double eeff, double z0)
        => extensionMeters * Math.Sqrt(eeff) / (MicrostripLoss.SpeedOfLight * z0);
}
