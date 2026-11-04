using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// The end-to-end gap between two microstrip lines of one width (MGAP, brief-sdl-9): a series capacitance
/// <c>Cg</c> across the gap and a shunt capacitance <c>Cp</c> from each line end to ground, a symmetric pi
/// network referenced to the two edges of the gap.
///
/// <para><b>Where the two capacitances come from.</b> The gap is symmetric about its centre plane, so it
/// is described completely by two excitations of it. Driven in phase (even mode), no current crosses the
/// centre plane — a magnetic wall — and each end sees only its own capacitance to ground, <c>Cp</c>.
/// Driven in antiphase (odd mode), the centre plane is at zero potential — an electric wall — and each
/// end sees <c>Cp</c> in parallel with the series capacitance taken to the mid-plane, <c>2·Cg</c>. Writing
/// <c>Ce</c> for the capacitance of the two ends together in the even mode and <c>Co</c> for one end in
/// the odd mode, <c>Cp = Ce/2</c> and <c>Cg = (Co − Cp)/2 = Co/2 − Ce/4</c>.</para>
///
/// <para><b>The two mode capacitances</b> are power laws in the gap-to-width ratio <c>g = S/W</c>, fitted
/// to field solutions on an εr = 9.6 substrate and carried to other permittivities by a power of
/// <c>εr/9.6</c> (the field sits partly in air, so the capacitance grows more slowly than εr). Per metre
/// of width, in pF/m, with <c>u = W/h</c>:</para>
/// <code>
///   Co/W = g^mo · exp(ko) · (εr/9.6)^0.8         mo = u·(0.619·log10 u − 0.3853),  ko = 4.26 − 1.453·log10 u
///   Ce/W = 12 · g^me · exp(ke) · (εr/9.6)^0.9    g ≤ 0.3:  me = 0.8675,             ke = 2.043·u^0.12
///                                                g > 0.3:  me = 1.565/u^0.16 − 1,    ke = 1.97 − 0.03/u
/// </code>
/// <para><b>Stated range</b>: 0.5 ≤ W/h ≤ 2, 0.1 ≤ S/W ≤ 1, 2.5 ≤ εr ≤ 15. Outside it the same
/// expressions are evaluated and the instance is told, once, that the answer is an extrapolation. The
/// even-mode law is two fits that meet at S/W = 0.3 with a step between them; it is kept as fitted, and
/// how far each side sits from an EM solve is recorded with the reference data
/// (<c>testdata/microstrip-gap</c>) rather than smoothed over here.</para>
///
/// <para><b>Two clamps, and both are physical.</b> Beyond S/W = 1 the even-mode capacitance is held at its
/// S/W = 1 value, so each end's capacitance to ground levels off as an open end's does rather than rising with the
/// fit. And at a gap wide against the line the series capacitance
/// becomes a small difference of two fitted numbers and can come out negative, which no geometry has.
/// <c>Cg</c> is held at zero there — the ends no longer see each other — and the range warning is what
/// says the fit has been left. The capacitances are quasi-static and lossless: they do not move with
/// frequency, and the gap's small radiation and dielectric loss are not modelled.</para>
/// </summary>
public static class MicrostripGap
{
    public static readonly ValidityRange WidthOverHeight = new(0.5, 2.0);
    public static readonly ValidityRange GapOverWidth = new(0.1, 1.0);
    public static readonly ValidityRange Permittivity = new(2.5, 15.0);

    /// <summary>Why a gap cannot be built, or null. <c>S ≤ 0</c> is two lines touching or overlapping — a joint, not
    /// a gap — and nothing in the closed form describes it (brief-sdl-9 D3).</summary>
    public static Diagnostic? Refusal(double wMeters, double sMeters, double hMeters)
    {
        if (!(sMeters > 0.0))
            return Diagnostic.Create("mgap.gap.not-positive", DiagnosticSeverity.Error,
                "MGAP: S={s} m is not a gap. S must be greater than zero, or the two lines touch.", ("s", sMeters));
        if (!(wMeters > 0.0))
            return Diagnostic.Create("mgap.width.not-positive", DiagnosticSeverity.Error,
                "MGAP: W={w} m must be positive.", ("w", wMeters));
        if (!(hMeters > 0.0))
            return Diagnostic.Create("mgap.height.not-positive", DiagnosticSeverity.Error,
                "MGAP: substrate H={h} m must be positive.", ("h", hMeters));
        return null;
    }

    /// <summary>The series capacitance across the gap and the shunt capacitance at each end, in farads.
    /// <paramref name="reporter"/> (optional) is told once about each ratio outside the stated range.</summary>
    public static (double SeriesC, double ShuntC) Capacitances(
        double wMeters, double sMeters, double hMeters, double epsR, MicrostripValidityReporter? reporter = null)
    {
        double u = wMeters / hMeters;
        double g = sMeters / wMeters;
        if (reporter is not null)
        {
            reporter.CheckRange("MGAP", "W/h", u, WidthOverHeight.Min, WidthOverHeight.Max);
            reporter.CheckRange("MGAP", "S/W", g, GapOverWidth.Min, GapOverWidth.Max);
            reporter.CheckRange("MGAP", "Er", epsR, Permittivity.Min, Permittivity.Max);
        }

        double lu = Math.Log10(u);
        double mo = u * (0.619 * lu - 0.3853);
        double ko = 4.26 - 1.453 * lu;
        double me, ke;
        if (g <= 0.3) { me = 0.8675; ke = 2.043 * Math.Pow(u, 0.12); }
        else          { me = 1.565 / Math.Pow(u, 0.16) - 1.0; ke = 1.97 - 0.03 / u; }

        const double PicoPerMetre = 1e-12;
        double co = wMeters * PicoPerMetre * Math.Pow(g, mo) * Math.Exp(ko) * Math.Pow(epsR / 9.6, 0.8);
        double ce = wMeters * PicoPerMetre * 12.0 * Math.Pow(g, me) * Math.Exp(ke) * Math.Pow(epsR / 9.6, 0.9);

        // Past the stated range's wide end the even-mode law keeps rising as g^me, while each end's capacitance to
        // ground should level off at an open end's: Ce is held at its S/W = 1 value there, so Cp stays bounded and an
        // optimiser walking S outward does not buy capacitance that no geometry has.
        if (g > GapOverWidth.Max)
            ce = wMeters * PicoPerMetre * 12.0 * Math.Exp(ke) * Math.Pow(epsR / 9.6, 0.9);

        double cp = ce / 2.0;
        double cg = Math.Max(0.0, co / 2.0 - ce / 4.0);
        return (cg, cp);
    }
}
