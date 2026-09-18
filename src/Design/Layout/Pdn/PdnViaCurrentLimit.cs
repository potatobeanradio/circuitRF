// What one barrel can carry, and the table that is the setting's opening value rather than the rule
// (docs/sonnet-briefs/brief-railrf-6-via-check.md R-rail6-2 / R-rail6-3, railrf.md §2.4, §4.2, Q-17,
//  Q-22).
//
// ── THIS IS A RULE WITH A STATED BASIS. IT IS NOT A THERMAL MODEL ──────────────────────────────
//
// §2.7, and the brief says it twice. Nothing here solves a heat equation, nothing here knows what
// the laminate conducts or what the planes sink, and no number computed here is a temperature. What
// it is: an allowable DC current DENSITY in the plated wall, stated at a rise budget, with the two
// corrections that follow from the barrel's own I²R heating.
//
// ── WHY THE TABLE IS NOT THE RULE, IN ITS OWN NUMBERS ──────────────────────────────────────────
//
// Review's answer to Q-17 is a table indexed on drill size at roughly a 10 °C rise:
//
//     0.2–0.3 mm  0.3–0.5 A      0.4–0.5 mm  0.7–1.0 A
//     0.6–0.8 mm  1.0–1.5 A      1.0–1.2 mm  1.5–2.5 A
//
// with a 0.3 mm drill AT 20 µm OF PLATING quoted separately at 0.8–1.0 A. That separate figure is
// the useful half of the answer because it disagrees with the table's own 0.3 mm row by about a
// factor of two, and the one term that differs is the plating thickness — the only thing that sets
// the barrel's conducting cross-section, and the one thing a table indexed on drill size cannot
// express. A 0.3 mm hole plated to 20 µm has roughly twice the annulus of the same hole at 10 µm.
//
// ── THE CONSTANT IS A READING OF THAT TABLE, NOT A NUMBER PICKED TO SUIT ───────────────────────
//
// At 50 A/mm² through the annulus, on a 1.6 mm board at a 10 °C rise, the table reproduces itself
// AT 10–12 µm OF PLATING, row for row:
//
//     drill   A(10 µm)     I        the row        A(12 µm)     I
//     0.2 mm  0.00597 mm²  0.298 A  0.3–0.5 A      0.00709 mm²  0.354 A
//     0.3 mm  0.00911 mm²  0.456 A  0.3–0.5 A      0.01086 mm²  0.543 A
//     0.4 mm  0.01225 mm²  0.613 A  0.7–1.0 A      0.01463 mm²  0.731 A
//     0.5 mm  0.01539 mm²  0.770 A  0.7–1.0 A      0.01840 mm²  0.920 A
//     0.6 mm  0.01854 mm²  0.927 A  1.0–1.5 A      0.02217 mm²  1.108 A
//     0.8 mm  0.02482 mm²  1.241 A  1.0–1.5 A      0.02971 mm²  1.485 A
//     1.0 mm  0.03110 mm²  1.555 A  1.5–2.5 A      0.03724 mm²  1.862 A
//     1.2 mm  0.03739 mm²  1.869 A  1.5–2.5 A      0.04478 mm²  2.239 A
//
// and the separately-quoted 0.3 mm at 20 µm comes out at 0.880 A, inside its own 0.8–1.0 A. So the
// whole of review's answer is one density at one plating thickness the table never stated, which is
// the finding §4.2 is making — and it is what makes 50 A/mm² a calibration rather than a guess.
//
// ── WHY LINEAR IN AREA, AND WHY THE TWO CORRECTIONS ARE SQUARE ROOTS ───────────────────────────
//
// The rule is a DENSITY, so the limit is linear in the annulus: that is §4.2's own "twice the
// annulus, twice the current", and it is the sentence a table indexed on drill size cannot say. The
// rise budget and the span move the allowable density rather than the area, and both move as square
// roots because the heating is I²R — accept four times the rise and you may carry twice the current;
// double the barrel's length and it dissipates twice as much at the same current, so it carries
// 1/√2 of it. The published ampacity charts this field has used for decades fit a rise exponent of
// 0.44, which is the same number to the accuracy anyone reads a chart at.
//
// A barrel's ACTUAL rise depends on where the heat goes, which railRF does not model and says so.
// That is why the table is carried alongside every computed limit as a SANITY BAND: a computed limit
// that wanders outside the band for its drill is reported as outside, never clamped to it.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>Which of the two readings produced a limit (R-rail6-4). <b>Two flags with different
/// bases side by side must not read identically</b>, which is why this is on the flag rather than
/// in a comment.</summary>
public enum PdnViaLimitBasis
{
    /// <summary>Computed from the barrel's own annulus, its span and the rise budget. The only basis
    /// that is a reading of THIS board's geometry.</summary>
    Computed,

    /// <summary>The drill-size table, because the geometry was not resolvable — nothing stated the
    /// plating thickness, and a limit computed from a defaulted one would be worth a factor of two
    /// and say nothing about it (Q-22).</summary>
    Table,
}

/// <summary>
/// One barrel's current limit, with every term that produced it and the band it is checked against.
/// </summary>
/// <param name="AmpsLimit">What the barrel may carry at <paramref name="RiseCelsius"/>.</param>
/// <param name="Basis">Computed from geometry, or the drill-size table.</param>
/// <param name="DrillMetres">The drilled hole diameter.</param>
/// <param name="PlatingMetres">The plated wall thickness used, or null where none was resolvable.</param>
/// <param name="PlatingBasis">Where that thickness came from — <b>printed every time</b> (R-rail6-3).</param>
/// <param name="SpanMetres">The z distance the barrel carries current over.</param>
/// <param name="RiseCelsius">The rise budget this limit is stated at.</param>
/// <param name="BandLowAmps">The table's own row for this drill, or null for a drill outside it.</param>
/// <param name="BandHighAmps">Same.</param>
public sealed record PdnViaLimit(
    double AmpsLimit,
    PdnViaLimitBasis Basis,
    double DrillMetres,
    double? PlatingMetres,
    PdnPlatingBasis? PlatingBasis,
    double SpanMetres,
    double RiseCelsius,
    double? BandLowAmps,
    double? BandHighAmps)
{
    /// <summary>The conducting annulus this limit was computed over, m². Zero on a table limit.</summary>
    public double AnnulusSquareMetres { get; init; }

    /// <summary>
    /// True where the computed limit sits inside the table's own row for this drill, false where it
    /// does not, null where the drill is outside the table's range.
    ///
    /// <para><b>False is not an error and the limit is NEVER clamped to the band</b> — the table
    /// hides the plating thickness and the computed limit does not, so a disagreement is the table
    /// being unable to express this barrel rather than the barrel being wrong.</para>
    /// </summary>
    public bool? InsideBand =>
        BandLowAmps is { } lo && BandHighAmps is { } hi
            ? AmpsLimit >= lo && AmpsLimit <= hi
            : null;

    /// <summary>The sentence a report prints — the number, the basis, and the plating, always.</summary>
    public string Describe()
    {
        string plating =
            PlatingMetres is { } t && PlatingBasis is { } pb
                ? PdnViaModel.DescribePlating(t, pb)
                : "no plating thickness was resolvable";

        string band = InsideBand switch
        {
            true  => $", inside the {BandLowAmps:0.##}–{BandHighAmps:0.##} A the drill-size guidance " +
                     "gives this drill",
            false => $", OUTSIDE the {BandLowAmps:0.##}–{BandHighAmps:0.##} A the drill-size guidance " +
                     "gives this drill — the guidance does not state a plating thickness and this " +
                     "does, so the two can differ by a factor of two and neither is clamped to the other",
            _     => ", and this drill is outside the range the drill-size guidance covers",
        };

        return Basis == PdnViaLimitBasis.Computed
            ? $"{AmpsLimit:0.###} A at a {RiseCelsius:0.#} °C rise, computed from a " +
              $"{DrillMetres * 1e3:0.###} mm drill over {SpanMetres * 1e3:0.###} mm with {plating}{band}"
            : $"{AmpsLimit:0.###} A, from the drill-size guidance for a {DrillMetres * 1e3:0.###} mm " +
              $"drill at roughly a {PdnViaCurrentLimit.ReferenceRiseCelsius:0.#} °C rise — " +
              $"{plating}, so there is no annulus to compute one from{band}";
    }
}

/// <summary>§4.2's limit: the barrel's own annulus, its span and a rise budget.</summary>
public static class PdnViaCurrentLimit
{
    /// <summary>
    /// 50 A/mm² through the plated annulus, on a <see cref="ReferenceSpanMetres"/> barrel at a
    /// <see cref="ReferenceRiseCelsius"/> rise.
    ///
    /// <para><b>Calibrated against review's own table, not chosen.</b> The file header works every
    /// row: at 10–12 µm of plating this density reproduces the whole table, and it puts the
    /// separately-quoted 0.3 mm at 20 µm at 0.880 A inside its own 0.8–1.0 A. The table's rows are
    /// therefore a 10 µm-plating table, which is exactly the factor of two §4.2 says the table
    /// hides.</para>
    /// </summary>
    public const double ReferenceDensityAmpsPerSquareMetre = 5.0e7;

    /// <summary>The rise review's table is quoted at, and the value the setting opens on.</summary>
    public const double ReferenceRiseCelsius = 10.0;

    /// <summary>1.6 mm — the board the table's figures are quoted through, and the span at which the
    /// span correction is unity.</summary>
    public const double ReferenceSpanMetres = 1.6e-3;

    /// <summary>
    /// Review's table, in metres and amps. <b>General engineering guidance rather than a standard
    /// held internally</b> (§4.2), which is why it is a sanity band drawn beside a computed limit and
    /// never the limit itself.
    /// </summary>
    private static readonly (double LoDrill, double HiDrill, double LoAmps, double HiAmps)[] Table =
    [
        (0.2e-3, 0.3e-3, 0.3, 0.5),   // a signal via
        (0.4e-3, 0.5e-3, 0.7, 1.0),
        (0.6e-3, 0.8e-3, 1.0, 1.5),
        (1.0e-3, 1.2e-3, 1.5, 2.5),
    ];

    /// <summary>
    /// The table's row for a drill, or null for one the table does not cover — <b>including the gaps
    /// BETWEEN its rows</b> (0.35 mm, 0.55 mm, 0.9 mm). Interpolating across a gap would invent a
    /// band review never gave, and a band is only worth drawing where it was actually stated.
    /// </summary>
    public static (double LowAmps, double HighAmps)? Band(double drillMetres)
    {
        foreach (var (lo, hi, a, b) in Table)
            if (drillMetres >= lo * 0.999 && drillMetres <= hi * 1.001)
                return (a, b);
        return null;
    }

    /// <summary>
    /// The limit for one barrel.
    ///
    /// <para><b>The plating basis decides which reading is used</b> (R-rail6-3, R-rail6-4). A
    /// thickness the stackup states or the document types gives a
    /// <see cref="PdnViaLimitBasis.Computed"/> limit over the real annulus; a
    /// <see cref="PdnPlatingBasis.Defaulted"/> one does NOT — the annulus would then rest on a number
    /// nobody checked and be worth a factor of two, so the limit falls back to the drill-size table
    /// and says so. Neither path produces a silent default.</para>
    ///
    /// <para>Null where there is nothing to compute from at all: no drill, or a defaulted plating on
    /// a drill the table does not cover. A missing limit is a note, never a flag (R-rail6-5's rule,
    /// applied to the other missing term).</para>
    /// </summary>
    /// <param name="drillMetres">The hole diameter.</param>
    /// <param name="platingMetres">The plated wall thickness.</param>
    /// <param name="platingBasis">Where it came from.</param>
    /// <param name="spanMetres">The z distance the barrel carries current over.</param>
    /// <param name="riseCelsius">The rise budget — <c>RailSettings.ViaTemperatureRiseCelsius</c>.</param>
    public static PdnViaLimit? Compute(
        double drillMetres, double platingMetres, PdnPlatingBasis platingBasis,
        double spanMetres, double riseCelsius)
    {
        if (!(drillMetres > 0)) return null;

        var band = Band(drillMetres);

        if (platingBasis == PdnPlatingBasis.Defaulted || !(platingMetres > 0) || !(spanMetres > 0) ||
            !(riseCelsius > 0))
        {
            if (band is not { } row) return null;

            // The table's own row, taken at its midpoint: the band is a range and a flag needs one
            // number, and the midpoint is the only choice that does not silently pick a side.
            return new PdnViaLimit(
                (row.LowAmps + row.HighAmps) / 2.0, PdnViaLimitBasis.Table,
                drillMetres, platingMetres > 0 ? platingMetres : null,
                platingMetres > 0 ? platingBasis : null,
                spanMetres, ReferenceRiseCelsius, row.LowAmps, row.HighAmps);
        }

        double area = PdnViaModel.AnnulusAreaSquareMetres(drillMetres, platingMetres);
        if (!(area > 0)) return null;

        double amps = ReferenceDensityAmpsPerSquareMetre * area
                    * Math.Sqrt(riseCelsius / ReferenceRiseCelsius)
                    * Math.Sqrt(ReferenceSpanMetres / spanMetres);

        return new PdnViaLimit(
            amps, PdnViaLimitBasis.Computed,
            drillMetres, platingMetres, platingBasis, spanMetres, riseCelsius,
            band?.LowAmps, band?.HighAmps)
        {
            AnnulusSquareMetres = area,
        };
    }
}
