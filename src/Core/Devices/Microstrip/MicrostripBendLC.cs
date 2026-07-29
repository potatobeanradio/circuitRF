namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Which corner geometry (and, by extension, which published electrical-model coefficients apply)
/// a bend uses — brief-mtaper-mklopf.md §1A. <c>None</c> = square corner (unmitered), <c>Fifty</c>
/// = a fixed 50% chamfer, <c>Optimal</c> = the Douville &amp; James W/h-dependent optimum
/// (<see cref="MicrostripDiscontinuities.MiterCutLength"/>).
/// </summary>
public enum MicrostripBendMiter { None, Fifty, Optimal }

/// <summary>
/// MBend's L-C-L equivalent-circuit coefficients — Kirschning, Jansen &amp; Koster, <i>Measurement
/// and computer-aided modeling of microstrip discontinuities by an improved resonator method</i>,
/// IEEE MTT-S 1983, pp. 495-497, as reproduced with full working in Poole &amp; Darwazeh, <i>Lecture
/// 3 - Practical Transmission Lines</i>, eqs (20)-(23) (<c>docs/sonnet-briefs/
/// Lecture-3-Practical-Transmission-Lines.pdf</c>, a genuine text-layer PDF, read directly — R1/R19).
///
/// <b>Units, verified rather than assumed (R-bnd-3):</b> plugging <c>W</c>/<c>h</c> in SI METRES
/// directly into eqs (20)-(23) as written yields C in the tens-of-femtofarad range and L in the
/// hundreds-of-femtohenry range for a typical W/h≈1-2 FR-4 bend — physically the right order of
/// magnitude for a published microstrip bend discontinuity (tens of fF / sub-nH), and consistent in
/// order of magnitude with the INDEPENDENT Gupta-Garg-Chadha bend model (eq 6.39/6.40,
/// <see cref="MicrostripDiscontinuities"/>'s own doc comment) evaluated at the same geometry — the
/// second, independent source R14 asks for. Millimetres would overshoot both by 1000×; this is the
/// verification, not an assumption.
///
/// <b>R-bnd-4 (the gap that is NOT papered over): no published closed-form electrical coefficients
/// exist for the OPTIMAL (Douville-James, W/h-dependent) miter percentage</b> — only 0% (None) and
/// 50% (Fifty) are in this source. <see cref="Compute"/> for <see cref="MicrostripBendMiter.Optimal"/>
/// therefore evaluates the SAME Fifty coefficients (the closest measured point) against the
/// Optimal geometry, and reports (once, loudly) that this is an approximation — never silently
/// borrowed. Douville &amp; James's own 1978 paper was not accessible from this environment to check
/// whether it separately characterises the compensated bend's residual reactance (§1A.3 of the
/// brief asks this be checked first); it was not found, so the Fifty-coefficient fallback stands.
///
/// <b>Validity range:</b> the source states one range (W/h = 0.2 to 6.0, εr = 2.36 to 10.4, up to
/// 14 GHz, ≈0.3% precision) directly after presenting BOTH the unmitered (20)-(21) and mitered
/// (22)-(23) pairs on the same slide — applied here to both, since it is the only range the source
/// gives and both pairs come from the same KJK resonator-method measurement campaign; this
/// extension is recorded here as an inference, not re-derived independently.
/// </summary>
public static class MicrostripBendLC
{
    public static readonly ValidityRange WOverHRange = new(0.2, 6.0);
    public static readonly ValidityRange EpsRRange = new(2.36, 10.4);
    public static readonly ValidityRange FreqRange = new(0.0, 14.0e9, "Hz");

    /// <summary>Computes the bend's series L (H, per side — NOT split; eq (25)'s own Z-matrix uses
    /// the full value on each side) and shunt C (F), plus whether the Optimal-miter fallback to the
    /// Fifty coefficients is in effect for this call (R-bnd-4).</summary>
    public static (double LHenries, double CFarads, bool ElectricalApproximated) Compute(
        double wMeters, double hMeters, double epsR, MicrostripBendMiter miter,
        MicrostripValidityReporter reporter)
    {
        double u = wMeters / hMeters;
        bool approximated = miter == MicrostripBendMiter.Optimal;
        // None -> its own (unmitered) coefficients; Fifty and Optimal both evaluate the Fifty
        // (50%-measured) coefficients — Optimal borrows them as the nearest available data point.
        bool useMiteredCoeffs = miter != MicrostripBendMiter.None;

        string modelName = useMiteredCoeffs ? "MBend(50%-mitered)" : "MBend(unmitered)";
        reporter.CheckRange(modelName, "W/h", u, WOverHRange.Min, WOverHRange.Max);
        reporter.CheckRange(modelName, "epsR", epsR, EpsRRange.Min, EpsRRange.Max);

        double cPicoFarads, lNanoHenries;
        if (useMiteredCoeffs)
        {
            // eq (22): 50%-mitered shunt C (pF).
            cPicoFarads = wMeters * ((3.93 * epsR + 0.62) * u + (7.6 * epsR + 3.80));
            // eq (23): 50%-mitered series L, per side (nH).
            lNanoHenries = 440.0 * hMeters * (1.0 - 1.062 * Math.Exp(-0.177 * Math.Pow(u, 0.947)));
        }
        else
        {
            // eq (20): unmitered shunt C (pF).
            cPicoFarads = wMeters * ((10.35 * epsR + 2.5) * u + (2.6 * epsR + 5.64));
            // eq (21): unmitered series L, per side (nH).
            lNanoHenries = 220.0 * hMeters * (1.0 - 1.35 * Math.Exp(-0.18 * Math.Pow(u, 1.39)));
        }

        return (lNanoHenries * 1e-9, cPicoFarads * 1e-12, approximated);
    }
}
