// Capacitance at the rail voltage, from a bias curve — and the three numbers a caller has to be
// able to show (docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-7; railrf.md §9, Q-12).
//
// ── Q-12 CLOSED THIS AS "CORRECT IT", AND §9 NAMES THE SIZE OF THE ERROR ───────────────────────
//
// "a 10 uF 0402 X5R can be UNDER 2 uF at its rated voltage. A PDN answer computed with the marked
// value is wrong by a factor of several in exactly the band the bulk capacitors own, and it is
// wrong OPTIMISTICALLY."
//
// So this is not a warning. The rail's voltage is known and capacitance-versus-bias curves are
// obtainable, so railRF APPLIES the curve at the rail voltage.
//
// ── THE RESIDUAL RISK IS COVERAGE, AND IT IS WHY Apply RETURNS A TRIPLE ────────────────────────
//
// §9, and this is the brief's real job: "a library that is ONLY PARTLY populated with curves
// produces a result that is PARTLY DERATED, and that is WORSE THAN EITHER EXTREME unless it is
// visible. Every part row shows marked, derated and which it used, and the result carries a count
// of parts with no curve."
//
// Hence RailDeratedCapacitance rather than a double. A FUNCTION RETURNING ONE NUMBER IS A FUNCTION
// WHOSE CALLER CANNOT SHOW THE OTHER TWO — and a parts table that could only show the number it
// used would make a partly-derated library indistinguishable from a fully-derated one, which is
// exactly the state §9 calls worse than either extreme.
//
// NUMBERS ARE BASE SI. Farads and volts.

namespace CircuitRF.Design.RailRf;

/// <summary>Which capacitance a part model is carrying (R-rail11-7). Shown on the part row beside
/// the other two, never instead of them.</summary>
public enum RailCapacitanceBasis
{
    /// <summary>The library row's marked value, because no bias curve was obtained for this part.
    /// <b>Warned about and counted</b> — this is the value §9 calls optimistic.</summary>
    Marked,

    /// <summary>The marked value corrected at the rail voltage through this part's own bias curve.
    /// What Q-12 asks for.</summary>
    Derated,

    /// <summary>Read out of the part's own Touchstone file, which was measured at whatever bias the
    /// vendor measured it at. <b>Not derated by railRF and not derateable</b>: the file states one
    /// curve and nothing in it says what bias produced it.</summary>
    Measured,
}

/// <summary>
/// One part's capacitance, <b>with all three of §9's numbers</b>: the marked value, the derated one
/// where a curve produced it, and which was used.
/// </summary>
/// <param name="MarkedFarads">What the library row states, or NaN where it states none.</param>
/// <param name="DeratedFarads">The curve's value at the rail voltage, or <b>null where there was no
/// curve to apply</b> — which is the case the count exists for.</param>
/// <param name="UsedFarads">The one the model carries.</param>
/// <param name="Basis">Which of the three <see cref="UsedFarads"/> is.</param>
/// <param name="RailVoltageV">The bias it was evaluated at, or null where none was known.</param>
/// <param name="Warning">What a reader should act on, or null. Never a note.</param>
public sealed record RailDeratedCapacitance(
    double MarkedFarads,
    double? DeratedFarads,
    double UsedFarads,
    RailCapacitanceBasis Basis,
    double? RailVoltageV,
    string? Warning)
{
    /// <summary>Derated over marked, or null where there was no curve. 0.2 is §9's own 10 µF part
    /// at its rated voltage.</summary>
    public double? Ratio =>
        DeratedFarads is { } d && MarkedFarads > 0 ? d / MarkedFarads : null;

    /// <summary>True where a curve was applied. <see cref="RailPartModelSet.WithoutBiasCurve"/>
    /// counts the parts where it was not.</summary>
    public bool CurveApplied => Basis == RailCapacitanceBasis.Derated;

    /// <summary>The row as the parts table prints it — <b>marked and derated side by side</b>, which
    /// is Q-12's own wording, with the one that was used said out loud.</summary>
    public string Describe() => Basis switch
    {
        RailCapacitanceBasis.Measured =>
            $"{Farads(UsedFarads)} read from the part's own file",
        RailCapacitanceBasis.Derated =>
            $"marked {Farads(MarkedFarads)}, derated {Farads(DeratedFarads!.Value)} at " +
            $"{RailVoltageV:0.###} V ({Ratio:P0} of marked) — the DERATED value was used",
        _ =>
            $"marked {Farads(MarkedFarads)}, no bias curve — the MARKED value was used",
    };

    internal static string Farads(double f) =>
        double.IsNaN(f)  ? "(none stated)"
        : f >= 1e-3      ? $"{f * 1e3:0.###} mF"
        : f >= 1e-6      ? $"{f * 1e6:0.###} µF"
        : f >= 1e-9      ? $"{f * 1e9:0.###} nF"
        :                  $"{f * 1e12:0.###} pF";
}

/// <summary>
/// Q-12's correction: a part's capacitance at the rail voltage, read off its own
/// capacitance-versus-bias curve.
/// </summary>
public static class RailDerating
{
    /// <summary>
    /// R-rail11-7. The triple for one library row at one rail voltage.
    ///
    /// <para><b>Never a single number</b> — see this file's header.</para>
    /// </summary>
    /// <param name="row">The library row. Its <see cref="PartLibraryRow.BiasCurve"/> is what is
    /// applied, and an empty one is the ordinary case rather than an error.</param>
    /// <param name="railVoltageV">The bias this part sits at, in VOLTS. Null where nothing on the
    /// rail stated a voltage, which is reported rather than defaulted to zero — <b>zero bias is the
    /// one voltage at which no derating happens at all</b>, so defaulting to it would silently
    /// produce the uncorrected answer under the corrected label.</param>
    public static RailDeratedCapacitance Apply(PartLibraryRow row, double? railVoltageV)
    {
        double marked = row.CapacitanceFarads ?? double.NaN;

        // ── A CURVE WITH NO MARKED VALUE IS NOT A CAPACITOR ─────────────────────────────────
        //
        // (field report, 2026-09-23.) A ferrite bead's row had picked up a one-point curve — the
        // editor used to seed one at 1 µF — and this returned that point as the DERATED value, so
        // the bead was solved as a 1 µF shunt capacitor. A row that states no capacitance is not
        // one; its curve is ignored and said to be.
        if (row.BiasCurve.Count > 0 && (!(marked > 0) || !row.IsCapacitor))
            return new RailDeratedCapacitance(
                marked, null, marked, RailCapacitanceBasis.Marked, railVoltageV,
                $"Part '{row.PartNumber}' has a bias curve and " +
                (row.IsCapacitor ? "states no capacitance" : $"is classed {PartLibraryRow.OtherClass}") +
                ", so its curve was ignored and it was NOT modelled as a derated capacitor.");

        if (row.BiasCurve.Count == 0)
            return new RailDeratedCapacitance(
                marked, null, marked, RailCapacitanceBasis.Marked, railVoltageV,
                $"Part '{row.PartNumber}' has no capacitance-versus-bias curve, so its MARKED " +
                "capacitance was used. An MLCC's capacitance falls with applied bias — a small-case " +
                "high-value part can be under a fifth of its marked value at its rated voltage — and " +
                "that error is optimistic.");

        if (railVoltageV is not { } bias)
            return new RailDeratedCapacitance(
                marked, null, marked, RailCapacitanceBasis.Marked, null,
                $"Part '{row.PartNumber}' has a bias curve and no rail voltage to apply it at, so " +
                "its MARKED capacitance was used. State a source's open-circuit voltage on this " +
                "rail and the curve is applied.");

        double? derated = CapacitanceAt(row.BiasCurve, bias);
        if (derated is not { } value)
            return new RailDeratedCapacitance(
                marked, null, marked, RailCapacitanceBasis.Marked, bias,
                $"Part '{row.PartNumber}' has a bias curve that carries no usable point, so its " +
                "MARKED capacitance was used.");

        string? warning = null;

        double top = row.BiasCurve.Max(p => p.BiasVolts);
        if (bias > top)
            warning = $"Part '{row.PartNumber}' sits at {bias:0.###} V and its bias curve stops at " +
                      $"{top:0.###} V, so the curve's last point was used. Capacitance keeps falling " +
                      "above the last point measured, so this is optimistic by an unknown amount.";

        if (row.VoltageRatingV is { } rating && bias > rating)
            warning = $"Part '{row.PartNumber}' is rated {rating:0.###} V and this rail sits at " +
                      $"{bias:0.###} V. The derated capacitance is the least of the problems with " +
                      "that; railRF applies the curve and does not judge the part's rating.";

        return new RailDeratedCapacitance(
            marked, value, value, RailCapacitanceBasis.Derated, bias, warning);
    }

    /// <summary>
    /// The curve's capacitance at one bias, in FARADS, or null where the curve carries no usable
    /// point.
    ///
    /// <para><b>Linear in volts between the two bracketing points</b>, and <b>clamped at both
    /// ends</b>. The clamps are not the same kind of thing and only one of them is reported: below
    /// the first point is an interpolation toward zero bias, where the curve is flattest and a
    /// vendor's own tool would answer the same; above the LAST point is an extrapolation of a curve
    /// that is still falling, so <see cref="Apply"/> warns and says which direction it is wrong in.
    /// Curves are read in the SORTED order of their bias, never in file order, because nothing
    /// requires a maintained table to be sorted.</para>
    /// </summary>
    public static double? CapacitanceAt(IReadOnlyList<PartBiasPoint> curve, double volts)
    {
        var points = curve.Where(p => p.CapacitanceFarads > 0)
                          .OrderBy(p => p.BiasVolts)
                          .ToList();
        if (points.Count == 0) return null;
        if (points.Count == 1) return points[0].CapacitanceFarads;

        if (volts <= points[0].BiasVolts)  return points[0].CapacitanceFarads;
        if (volts >= points[^1].BiasVolts) return points[^1].CapacitanceFarads;

        for (int i = 1; i < points.Count; i++)
        {
            var lo = points[i - 1];
            var hi = points[i];
            if (volts > hi.BiasVolts) continue;

            double span = hi.BiasVolts - lo.BiasVolts;
            if (!(span > 0)) return hi.CapacitanceFarads;

            double t = (volts - lo.BiasVolts) / span;
            return lo.CapacitanceFarads + t * (hi.CapacitanceFarads - lo.CapacitanceFarads);
        }

        return points[^1].CapacitanceFarads;
    }
}
