// The SMT case-size table — brief-footprint-1-land-pattern-generator.md R-fp1-1.
//
// DATA, in one place. Every number here is a case-size nominal from the part's own outline
// drawing; nothing in this file computes a land pattern (that is LandPattern) and nothing draws
// (that is ChipLandPatternGenerator). Framework-free by construction, like the rest of src/Design.
//
// Dimensions are `decimal` millimetres rather than double, for the same reason LayoutUnits does its
// arithmetic in decimal: 1.25 mm and 0.125 mm are exact in decimal and are not in binary floating
// point, and the PCell contract's R5 determinism rule makes "the same case must land on the same
// DBU on every machine" a correctness property rather than a nicety.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>Which family a case code belongs to — what its code MEANS, not how big it is.</summary>
public enum SmtCaseFamily
{
    /// <summary>A two-terminal chip: an imperial code, terminations wrapped around the SHORT
    /// edges.</summary>
    Chip,

    /// <summary>A reverse-geometry chip: an imperial code, terminations on the LONG edges, so the
    /// mounting loop is short. Geometrically this is just a chip whose body is wider than it is
    /// long — nothing downstream special-cases it.</summary>
    ReverseGeometry,

    /// <summary>A larger MLCC body. An imperial code; a chip in every other respect.</summary>
    Mlcc,

    /// <summary>A moulded tantalum/polymer body. The code is ALREADY metric
    /// (<c>3216-18</c> = 3.2 x 1.6 x 1.8 mm) and its twin column is the EIA letter — R-fp1-1b.</summary>
    MouldedTantalum,

    /// <summary>
    /// A two-pad SMD quartz crystal in a sealed ceramic package. The code is metric and carries an
    /// <c>XTAL</c> prefix so it cannot collide with the CHIP code of the same body: 3.2 x 1.6 mm is
    /// both a crystal package and imperial <c>1206</c>, and <c>FootprintTokens</c> already reports a
    /// bare <c>3216</c> as ambiguous for exactly that reason.
    ///
    /// <para><b>Its terminations do not wrap.</b> The electrodes are metallized on the UNDERSIDE
    /// only, so no toe fillet forms up a side face and no side fillet forms at all — which is why
    /// this family has its own goals in <c>FilletGoals</c> rather than the chip set.</para>
    /// </summary>
    Crystal,

    /// <summary>
    /// A two-land wire jumper — a bare link or a 0 ohm part bridging two nets. <b>It has no case and
    /// no body</b>: what identifies it is the PAD PITCH, which is what its code states, so for this
    /// family alone <see cref="SmtCase.BodyLengthMm"/> reads as that pitch. See its remarks.
    /// </summary>
    WireJumper,
}

/// <summary>
/// One case size: what the code says the part is, and the two dimensions a land pattern is computed
/// from — the body, and the termination band.
/// </summary>
/// <param name="Code">The case code as a designer writes it: <c>0402</c>, <c>0306</c>,
/// <c>3216-18</c>.</param>
/// <param name="MetricTwin">The OTHER name this case is known by: the metric code for an imperial
/// case (<c>0402</c> -> <c>1005</c>), the EIA LETTER for a tantalum, whose code is already metric
/// (R-fp1-1b), the plain metric body code for a crystal (<c>XTAL3216</c> -> <c>3216</c>), and, for a
/// wire jumper — which has no second naming scheme at all — what the part IS. Carried on every row
/// because the imperial/metric collision is a silent 2.4x error — see the series overview §1e.</param>
/// <param name="BodyLengthMm">Along the TERMINATION axis: the direction the two lands are separated
/// in. For a reverse-geometry case this is the SHORT body dimension, which is exactly why reverse
/// geometry needs no special case anywhere.
///
/// <para><b>For <see cref="SmtCaseFamily.WireJumper"/> this column is the LAND PITCH.</b> A wire
/// jumper has no body to measure — the link that sits across it spans exactly pad centre to pad
/// centre, so the two numbers are one number, and a second column carrying it is a column that can
/// drift out of step with the code. <c>LandPattern</c> holds it EXACTLY: a jumper's pitch is its
/// name, so unlike a chip's it must not move with the density level.</para></param>
/// <param name="BodyWidthMm">Across the termination axis.</param>
/// <param name="BodyToleranceMm">One symmetric tolerance for both body dimensions. A deliberate
/// simplification of the part drawing's separate L and W tolerances: the two are equal or within a
/// hair of it on every row here, and one column that is right is better than two that are
/// hand-maintained.</param>
/// <param name="TerminationLengthMm">The metallized band's extent ALONG the termination axis — what
/// sets the inner (heel) edge of the land, and the number a land pattern is most sensitive to. On a
/// crystal it is the underside ELECTRODE, which does not wrap; on a wire jumper, which has no part to
/// measure, it is the nominal land length the density level then grows or shrinks.</param>
/// <param name="TerminationToleranceMm">Symmetric tolerance on <paramref name="TerminationLengthMm"/>.</param>
/// <param name="TerminationWidthMm">The band's extent ACROSS the termination axis, when it is
/// narrower than the body — a moulded tantalum's is, and so is a crystal's electrode and a wire
/// jumper's land. Null means the band wraps the full body width, which is true of every chip.</param>
/// <param name="BodyHeightMm">Stated only where the CODE states it — a tantalum's <c>-18</c>. Null
/// elsewhere: a chip's height is a part property, not a case property, and inventing one would put a
/// number in the table that no code backs.</param>
public sealed record SmtCase(
    string Code,
    string MetricTwin,
    SmtCaseFamily Family,
    decimal BodyLengthMm,
    decimal BodyWidthMm,
    decimal BodyToleranceMm,
    decimal TerminationLengthMm,
    decimal TerminationToleranceMm,
    decimal? TerminationWidthMm = null,
    decimal? BodyHeightMm = null)
{
    /// <summary>True when <see cref="Code"/> is itself a metric code and <see cref="MetricTwin"/> is
    /// therefore a letter rather than a second number (R-fp1-1b). A crystal's code is metric too, but
    /// it says so in the code itself (<c>XTAL3216</c>) and its twin column is still a number, so it is
    /// NOT one of these — this property is about the twin column's reading, not about the units.</summary>
    public bool CodeIsMetric => Family == SmtCaseFamily.MouldedTantalum;

    /// <summary>True where the two lands are metallized on the part's UNDERSIDE and do not wrap up a
    /// side face, so no toe or side fillet can form — a crystal, and a wire jumper, which has no part
    /// at all. What <c>FilletGoals</c> branches on.</summary>
    public bool TerminationsAreBottomOnly
        => Family is SmtCaseFamily.Crystal or SmtCaseFamily.WireJumper;

    /// <summary>The termination band's width across the axis — the body width for a wrapped chip
    /// termination, which is what a null <see cref="TerminationWidthMm"/> means.</summary>
    public decimal EffectiveTerminationWidthMm => TerminationWidthMm ?? BodyWidthMm;

    /// <summary>
    /// What a combobox row, a tooltip and a refusal all read — the overview's §1e spelling, with the
    /// twin and the millimetres on every mention, because <c>0201</c> alone is ambiguous between two
    /// real case sizes that differ by 2.4x.
    /// </summary>
    public string Display => Family switch
    {
        // A tantalum's code is already metric, so the twin column is the EIA letter (R-fp1-1b), and
        // the code's trailing group states a height the other families do not have.
        SmtCaseFamily.MouldedTantalum =>
            $"{Code} ({MetricTwin})   {Mm(BodyLengthMm)} x {Mm(BodyWidthMm)} x {Mm(BodyHeightMm ?? 0m)} mm",

        // A crystal's code carries its own metric body, so printing "metric 3216" beside XTAL3216
        // would say the same thing twice; what a reader needs instead is the part KIND, because the
        // same 3.2 x 1.6 mm body is also imperial 1206 and the two land patterns are not alike.
        SmtCaseFamily.Crystal =>
            $"{Code} (2-pad crystal)   {Mm(BodyLengthMm)} x {Mm(BodyWidthMm)} mm",

        // A jumper has no body, so there is no "L x W" to print — the pitch IS the part.
        SmtCaseFamily.WireJumper =>
            $"{Code} ({MetricTwin})   {Mm(BodyLengthMm)} mm pad pitch",

        _ => $"{Code} (metric {MetricTwin})   {Mm(BodyLengthMm)} x {Mm(BodyWidthMm)} mm",
    };

    // Two decimals is the reading of a case size everywhere else in the industry ("1.00 x 0.50 mm");
    // three only where a third one is real, which on this table is 008004's 0.125 mm alone. Formatting
    // it away would print "0.13", and a reader comparing that against a part drawing would rightly
    // stop and check.
    private static string Mm(decimal v)
        => v.ToString(decimal.Round(v, 2) == v ? "0.00" : "0.000",
                      System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Every case size circuitRF generates a land pattern for (R-fp1-1). <b>Two lands on every row</b> —
/// §8 of the brief: a multi-pin package is a different land-pattern problem, and Component Import
/// already covers it for anyone who has the data. <c>FootprintCatalog.BuiltInPadCount</c> is the one
/// place that number is written down.
/// </summary>
public static class SmtCaseTable
{
    /// <summary>The series default — the case a picker opens on.</summary>
    public const string DefaultCode = "0201";

    private static readonly SmtCase[] _all =
    [
        // ── Two-terminal chips, imperial codes ──────────────────────────────────────────────────
        //                Code      twin    family                    L        W       tol      T       Ttol
        new SmtCase("008004", "0201", SmtCaseFamily.Chip,            0.25m,  0.125m, 0.015m, 0.060m, 0.020m),
        new SmtCase("01005",  "0402", SmtCaseFamily.Chip,            0.40m,  0.20m,  0.020m, 0.100m, 0.030m),
        new SmtCase("0201",   "0603", SmtCaseFamily.Chip,            0.60m,  0.30m,  0.030m, 0.150m, 0.050m),
        new SmtCase("0402",   "1005", SmtCaseFamily.Chip,            1.00m,  0.50m,  0.050m, 0.250m, 0.100m),
        new SmtCase("0603",   "1608", SmtCaseFamily.Chip,            1.60m,  0.80m,  0.100m, 0.350m, 0.150m),
        new SmtCase("0805",   "2012", SmtCaseFamily.Chip,            2.00m,  1.25m,  0.100m, 0.400m, 0.200m),
        new SmtCase("1206",   "3216", SmtCaseFamily.Chip,            3.20m,  1.60m,  0.150m, 0.500m, 0.250m),
        new SmtCase("1210",   "3225", SmtCaseFamily.Chip,            3.20m,  2.50m,  0.150m, 0.500m, 0.250m),
        new SmtCase("1812",   "4532", SmtCaseFamily.Chip,            4.50m,  3.20m,  0.200m, 0.600m, 0.250m),
        // W > L: the code is inches L x W and this one is wider than it is long. Nothing here cares.
        new SmtCase("1825",   "4564", SmtCaseFamily.Chip,            4.50m,  6.40m,  0.200m, 0.600m, 0.250m),
        new SmtCase("2010",   "5025", SmtCaseFamily.Chip,            5.00m,  2.50m,  0.200m, 0.600m, 0.250m),
        new SmtCase("2512",   "6332", SmtCaseFamily.Chip,            6.30m,  3.20m,  0.200m, 0.600m, 0.250m),

        // ── Reverse geometry: terminations on the LONG edges ────────────────────────────────────
        // These exist to shorten the mounting loop, which is the quantity railRF's Q2 is entirely
        // about — a power-integrity tool that cannot draw one is missing the interesting half.
        // BodyLengthMm is still "along the termination axis", so it is the SHORT dimension here.
        new SmtCase("0306",   "0816", SmtCaseFamily.ReverseGeometry, 0.80m,  1.60m,  0.100m, 0.200m, 0.050m),
        new SmtCase("0508",   "1220", SmtCaseFamily.ReverseGeometry, 1.25m,  2.00m,  0.100m, 0.300m, 0.100m),
        new SmtCase("0612",   "1632", SmtCaseFamily.ReverseGeometry, 1.60m,  3.20m,  0.150m, 0.350m, 0.150m),

        // ── Larger MLCC bodies — where bulk ceramics live ───────────────────────────────────────
        new SmtCase("1808",   "4520", SmtCaseFamily.Mlcc,            4.50m,  2.00m,  0.200m, 0.600m, 0.250m),
        new SmtCase("2220",   "5750", SmtCaseFamily.Mlcc,            5.70m,  5.00m,  0.250m, 0.650m, 0.250m),
        new SmtCase("2225",   "5763", SmtCaseFamily.Mlcc,            5.70m,  6.30m,  0.250m, 0.650m, 0.250m),

        // ── Moulded tantalum / polymer, EIA metric codes ────────────────────────────────────────
        // The twin column is the LETTER (R-fp1-1b) and the code's trailing group is the HEIGHT. The
        // termination band is narrower than the body on all of them, which is why they are the only
        // rows that state TerminationWidthMm.
        new SmtCase("3216-18", "A", SmtCaseFamily.MouldedTantalum, 3.20m, 1.60m, 0.200m, 0.800m, 0.300m, 1.20m, 1.80m),
        new SmtCase("3528-21", "B", SmtCaseFamily.MouldedTantalum, 3.50m, 2.80m, 0.200m, 0.800m, 0.300m, 2.20m, 2.10m),
        new SmtCase("6032-28", "C", SmtCaseFamily.MouldedTantalum, 6.00m, 3.20m, 0.300m, 1.300m, 0.300m, 2.20m, 2.80m),
        new SmtCase("7343-31", "D", SmtCaseFamily.MouldedTantalum, 7.30m, 4.30m, 0.300m, 1.300m, 0.300m, 2.40m, 3.10m),
        new SmtCase("7343-43", "X", SmtCaseFamily.MouldedTantalum, 7.30m, 4.30m, 0.300m, 1.300m, 0.300m, 2.40m, 4.30m),

        // ── Two-pad SMD quartz crystals, metric codes behind an XTAL prefix ─────────────────────
        // The prefix is not decoration: 3.2 x 1.6 mm is ALSO imperial 1206, and FootprintTokens
        // already reports a bare `3216` as ambiguous between the two readings. A crystal row coded
        // `3216` would have made that ambiguity three-way and silent, since the chip and the crystal
        // are the same size and only their LAND PATTERNS differ.
        //
        // The termination columns are the underside ELECTRODE, not a wrapped band — these parts are
        // a sealed ceramic package with metallization on the bottom face only, which is what
        // FilletGoals branches on. The dimensions are the generic ones for each package size rather
        // than any one part's; a specific crystal's drawing may differ by a tenth, and that is what
        // the density levels are for.
        //                Code        twin    family                  L       W      tol      T       Ttol    Tw
        new SmtCase("XTAL3216", "3216", SmtCaseFamily.Crystal,      3.20m,  1.60m, 0.100m, 1.100m, 0.100m, 1.20m),
        new SmtCase("XTAL2016", "2016", SmtCaseFamily.Crystal,      2.00m,  1.60m, 0.100m, 0.700m, 0.100m, 1.10m),

        // ── Wire jumper ────────────────────────────────────────────────────────────────────────
        // Two lands for a bare link or a 0 ohm part. There is no case and no body, so the "L" column
        // is the PAD PITCH — see SmtCase.BodyLengthMm — and LandPattern holds it exactly rather than
        // deriving it, because the pitch is the part's name and a density level that moved it would
        // produce a land pattern the link no longer reaches across. The termination columns are the
        // nominal land, which IS what the density level grows and shrinks.
        //                Code        twin            family                pitch   W      tol      T       Ttol    Tw
        new SmtCase("JUMPER2.6", "wire link", SmtCaseFamily.WireJumper, 2.60m, 1.20m, 0.000m, 1.000m, 0.000m, 1.20m),
    ];

    /// <summary>Every case, in table order — which is by family and then by size, and is the order a
    /// picker lists them in. Deliberately not sorted at runtime: the reading order of the table IS
    /// the intended order, and re-deriving it would put <c>008004</c> next to <c>0805</c>.</summary>
    public static IReadOnlyList<SmtCase> All => _all;

    /// <summary>Every code, in table order — what a refusal lists (R-fp1-5a).</summary>
    public static IReadOnlyList<string> Codes { get; } = _all.Select(c => c.Code).ToArray();

    private static readonly Dictionary<string, SmtCase> _byCode =
        _all.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The case with this code, or null. Case-insensitive; never throws.</summary>
    public static SmtCase? Find(string? code)
        => code is { Length: > 0 } && _byCode.TryGetValue(code.Trim(), out var c) ? c : null;
}
