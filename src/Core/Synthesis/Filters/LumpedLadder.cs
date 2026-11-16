using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>One part of a lumped ladder's arm: an L in henries or a C in farads.</summary>
public sealed record LumpedPart(PlanSiteRole Role, double Value);

/// <summary>One arm of a lumped ladder, in order from pin 1: in the line (series) or to ground (shunt), its parts in
/// series with each other or in parallel.</summary>
public sealed record LumpedArm(bool Shunt, bool Parallel, IReadOnlyList<LumpedPart> Parts);

/// <summary>
/// A lumped LC ladder at Z0 from a <see cref="PrototypeLadder"/> (docs/design/filter-synthesis.md §1): each prototype
/// element scaled to Z0 and moved to the band. <see cref="LoadOhms"/> is the termination it is designed into, Z0 except
/// where g(N+1) ≠ 1 (an even-order Chebyshev).
/// </summary>
public sealed record LumpedLadder(IReadOnlyList<LumpedArm> Arms, double LoadOhms)
{
    /// <summary>The ladder for <paramref name="spec"/>, its first arm in the line when <paramref name="seriesFirst"/>.</summary>
    public static LumpedLadder For(FilterSpec spec, bool seriesFirst)
        => Transform(PrototypeLadder.For(spec.Response, spec.Order, spec.RippleDb, spec.StopAttenuationDb),
            spec.Band, spec.F1Hz, spec.F2Hz, spec.Z0, seriesFirst);

    /// <summary>
    /// The frequency and impedance transforms, each prototype reactance on its own (ω1 = 2πF1 is a lowpass's or a
    /// highpass's cutoff; ω0 = 2π√(F1·F2) and B = 2π(F2 − F1) a band's centre and width):
    /// <code>
    ///   lowpass    L·Z0/ω1                     C/(Z0·ω1)
    ///   highpass   a C of 1/(L·Z0·ω1)          an L of Z0/(C·ω1)
    ///   bandpass   L·Z0/B in series with       C/(Z0·B) in parallel with
    ///              a C of B/(L·Z0·ω0²)         an L of Z0·B/(C·ω0²)
    ///   bandstop   L·Z0·B/ω0² in parallel with C·B/(Z0·ω0²) in series with
    ///              a C of 1/(L·Z0·B)           an L of Z0/(C·B)
    /// </code>
    /// with L and C the prototype's normalised values (Matthaei, Young and Jones, §4.04; a book, no DOI). A prototype's
    /// resonators (a ladder with finite zeros) are moved to a lowpass or a highpass only: in a band each would nest a
    /// resonator in a resonator, which is refused (<c>synthesis.proto.zeros-in-band</c>).
    /// </summary>
    public static LumpedLadder Transform(PrototypeLadder p, FilterBand band, double f1Hz, double f2Hz, double z0,
        bool seriesFirst)
    {
        bool isBand = band is FilterBand.Bandpass or FilterBand.Bandstop;
        if (!(z0 > 0.0) || !(f1Hz > 0.0) || isBand && !(f2Hz > f1Hz) || !double.IsFinite(f2Hz))
            throw PrototypeLadder.Refuse("synthesis.proto.spec",
                "Lumped ladder: Z0 = {z0} Ω and the edges F1 = {f1} Hz, F2 = {f2} Hz need Z0 > 0 and 0 < F1, and 0 < F1 < F2 for a band.",
                ("z0", z0), ("f1", f1Hz), ("f2", f2Hz));
        if (isBand && p.Omega.Any(double.IsFinite))
            throw PrototypeLadder.Refuse("synthesis.proto.zeros-in-band",
                "Lumped ladder: a prototype with finite zeros is built as a lowpass or a highpass; as a {band} each of its resonators would sit in another.",
                ("band", band.ToString().ToLowerInvariant()));

        double w1 = 2.0 * Math.PI * f1Hz, w0 = 2.0 * Math.PI * Math.Sqrt(f1Hz * f2Hz), b = 2.0 * Math.PI * (f2Hz - f1Hz);
        LumpedPart L(double v) => new(PlanSiteRole.L, v);
        LumpedPart C(double v) => new(PlanSiteRole.C, v);
        // One normalised reactance (an L, or a C) in real parts.
        LumpedPart[] Map(bool isL, double v) => (band, isL) switch
        {
            (FilterBand.Lowpass, true) => [L(v * z0 / w1)],
            (FilterBand.Lowpass, false) => [C(v / (z0 * w1))],
            (FilterBand.Highpass, true) => [C(1.0 / (v * z0 * w1))],
            (FilterBand.Highpass, false) => [L(z0 / (v * w1))],
            (FilterBand.Bandpass, true) => [L(v * z0 / b), C(b / (v * z0 * w0 * w0))],
            (FilterBand.Bandpass, false) => [C(v / (z0 * b)), L(z0 * b / (v * w0 * w0))],
            (_, true) => [L(v * z0 * b / (w0 * w0)), C(1.0 / (v * z0 * b))],
            _ => [C(v * b / (z0 * w0 * w0)), L(z0 / (v * b))],
        };

        var arms = new List<LumpedArm>();
        for (int k = 1; k <= p.Order; k++)
        {
            bool shunt = k % 2 == 1 != seriesFirst;
            double g = p.G[k], om = p.Omega[k];
            if (double.IsFinite(om))   // a tank in the line, or its dual, a trap to ground
                arms.Add(new LumpedArm(shunt, !shunt, [.. Map(!shunt, g), .. Map(shunt, 1.0 / (g * om * om))]));
            else   // a band's pair: in series for a bandpass's series arm and a bandstop's shunt arm, else in parallel
                arms.Add(new LumpedArm(shunt, isBand && shunt == (band == FilterBand.Bandpass), Map(!shunt, g)));
        }
        bool lastShunt = p.Order % 2 == 1 != seriesFirst;
        return new LumpedLadder(arms, lastShunt ? p.G[^1] * z0 : z0 / p.G[^1]);
    }
}
