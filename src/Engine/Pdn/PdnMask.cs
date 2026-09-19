// The target as a mask, and the violations with their margins in dB
// (docs/design/railrf.md §2.2 "The target", §2.4 "Over frequency", §9;
//  docs/sonnet-briefs/brief-railrf-12-impedance.md R-rail12-3, R-rail12-7).
//
// ── NO DOMAIN TYPES ────────────────────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a), so this file names no RailTarget and no
// RailLoad: it takes frequencies and ohms and gives back rows. The caller turns a target of
// whichever of §2.2's four kinds into one of these — which is also the boundary that makes
// "masks are PER OBSERVATION PORT" cheap to honour, since a mask here is just a value a port
// carries.
//
// ── A MARGIN CARRIES WHETHER IT IS INDICATIVE, AND THE TYPE IS WHY ─────────────────────────────
//
// §9: "a mask margin in dB computed from an INDICATIVE peak looks exactly as authoritative as a
// real one." R-rail12-7 turns that into a rule about the TYPE rather than about discipline — a
// margin type that cannot carry the flag is the wrong type — so the flag is a field on every row
// and on the report, not something a caller is trusted to print beside it.

namespace CircuitRF.Engine.Pdn;

/// <summary>One row of a piecewise mask: a frequency in HERTZ and a limit in OHMS, both base SI.</summary>
public readonly record struct PdnMaskPoint(double FrequencyHz, double LimitOhms);

/// <summary>
/// The impedance ceiling a port is judged against.
///
/// <para><b>Interpolated in LOG f and LOG ohms</b> between its stated points. A PDN mask is drawn,
/// quoted and reasoned about on log-log axes — "flat to 1 MHz, then rising" is a straight line
/// THERE and a curve anywhere else — so a linear interpolation between two points a decade apart
/// would sit up to 30 % away from the ceiling a reader believes they drew, in the direction that
/// passes a design that does not meet it.</para>
///
/// <para><b>Outside the stated band nothing is judged</b>, and that is not the same as passing.
/// <see cref="LimitAt"/> returns null there and <see cref="PdnMaskReport.UnjudgedPoints"/> counts
/// it, because a sweep that runs a decade past the mask's last point would otherwise report a
/// clean pass over frequencies the target never spoke about.</para>
/// </summary>
public sealed class PdnMask
{
    private readonly PdnMaskPoint[] _points;

    private PdnMask(PdnMaskPoint[] points, string description)
    {
        _points = points;
        Description = description;
    }

    /// <summary>The stated points, ascending in frequency.</summary>
    public IReadOnlyList<PdnMaskPoint> Points => _points;

    /// <summary>What a plot's legend and an export's provenance call this mask.</summary>
    public string Description { get; }

    /// <summary>The bottom of the band this mask judges, in hertz.</summary>
    public double StartHz => _points[0].FrequencyHz;

    /// <summary>The top of it.</summary>
    public double StopHz => _points[^1].FrequencyHz;

    /// <summary>
    /// A flat <c>Z_target</c> over the whole of a band — §2.2's flat-impedance target, and what a
    /// transient target's <c>ΔV/ΔI</c> derives to.
    /// </summary>
    /// <param name="ohms">The ceiling.</param>
    /// <param name="startHz">The bottom of the band it applies over.</param>
    /// <param name="stopHz">The top. <b>For a transient target this is the KNEE frequency</b>
    /// <c>0.35/t_rise</c> and not the sweep's own top: the classic PDN target says nothing about
    /// frequencies the load's own edge cannot reach, and extending it upward would report
    /// violations against a limit nobody stated.</param>
    public static PdnMask Flat(double ohms, double startHz, double stopHz) =>
        new([new PdnMaskPoint(startHz, ohms), new PdnMaskPoint(stopHz, ohms)],
            $"{ohms * 1e3:0.###} mΩ flat, {Hertz(startHz)} to {Hertz(stopHz)}");

    /// <summary>
    /// §2.2's piecewise mask. At least two points, ascending in frequency, every one positive.
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two usable points.</exception>
    public static PdnMask Piecewise(IEnumerable<PdnMaskPoint> points, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(points);

        var sorted = points
            .Where(p => p.FrequencyHz > 0 && p.LimitOhms > 0 &&
                        double.IsFinite(p.FrequencyHz) && double.IsFinite(p.LimitOhms))
            .OrderBy(p => p.FrequencyHz)
            .ToArray();

        if (sorted.Length < 2)
            throw new ArgumentException(
                "A piecewise mask needs at least two usable points so it spans a band.", nameof(points));

        return new PdnMask(
            sorted,
            description ?? $"{sorted.Length}-point mask, {Hertz(sorted[0].FrequencyHz)} to " +
                           $"{Hertz(sorted[^1].FrequencyHz)}");
    }

    /// <summary>
    /// The ceiling at one frequency, in OHMS, or <b>null outside the stated band</b> — see this
    /// type's own note for why that is not a pass.
    /// </summary>
    public double? LimitAt(double frequencyHz)
    {
        if (!(frequencyHz > 0) || frequencyHz < StartHz || frequencyHz > StopHz) return null;

        for (int i = 1; i < _points.Length; i++)
        {
            if (frequencyHz > _points[i].FrequencyHz) continue;

            var (f0, z0) = (_points[i - 1].FrequencyHz, _points[i - 1].LimitOhms);
            var (f1, z1) = (_points[i].FrequencyHz, _points[i].LimitOhms);
            if (f1 <= f0) return z1;

            double t = (Math.Log(frequencyHz) - Math.Log(f0)) / (Math.Log(f1) - Math.Log(f0));
            return Math.Exp(Math.Log(z0) + t * (Math.Log(z1) - Math.Log(z0)));
        }

        return _points[^1].LimitOhms;
    }

    /// <summary>
    /// The margin in DECIBELS: <c>20·log₁₀(limit / |Z|)</c>.
    ///
    /// <para><b>Positive is headroom and negative is a violation</b>, which is the sense §2.6's
    /// worked example reads in — <i>"still passes at 1.8 dB"</i> — and the sense a removal ranking
    /// needs, since taking a part off can only move the margin down.</para>
    /// </summary>
    public static double MarginDb(double limitOhms, double magnitudeOhms) =>
        !(limitOhms > 0) || !(magnitudeOhms > 0)
            ? double.NaN
            : 20.0 * Math.Log10(limitOhms / magnitudeOhms);

    /// <summary>
    /// Judges one port's curve.
    ///
    /// <para><b>One row per contiguous EXCURSION, at its worst point</b> — not one per sample. A
    /// single peak crossing the ceiling covers tens of grid points, and a table with forty rows for
    /// one problem is a table nobody reads; worse, the count would move with the sweep's point
    /// density, which is a setting rather than a property of the design.</para>
    /// </summary>
    /// <param name="mask">The ceiling, or null where this port states none — in which case nothing
    /// is judged and the report says so rather than passing.</param>
    /// <param name="frequenciesHz">The sweep's own grid, ascending.</param>
    /// <param name="magnitudeOhms">|Z| at each of those frequencies.</param>
    /// <param name="indicative">True where any number behind this curve is a class-default ESR
    /// (§9, R-rail11-4). <b>Carried onto every row</b>, because a margin is exactly the number that
    /// looks authoritative whether or not it is.</param>
    public static PdnMaskReport Judge(
        PdnMask? mask, double[] frequenciesHz, double[] magnitudeOhms, bool indicative)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ArgumentNullException.ThrowIfNull(magnitudeOhms);

        if (mask is null)
            return new PdnMaskReport([], null, null, 0, frequenciesHz.Length, indicative, false);

        int n = Math.Min(frequenciesHz.Length, magnitudeOhms.Length);
        var rows = new List<PdnMaskViolation>();

        int judged = 0, unjudged = 0;
        double? worstMargin = null, worstHz = null;

        // The excursion currently open, or -1. Closed by the first sample that is inside the mask
        // or outside its band, and by the end of the sweep.
        int openAt = -1;
        double openWorst = 0, openWorstHz = 0, openWorstZ = 0, openWorstLimit = 0;

        for (int i = 0; i < n; i++)
        {
            double? limit = mask.LimitAt(frequenciesHz[i]);
            if (limit is not { } lim || !(magnitudeOhms[i] > 0) || !double.IsFinite(magnitudeOhms[i]))
            {
                unjudged++;
                Close();
                continue;
            }

            judged++;
            double margin = MarginDb(lim, magnitudeOhms[i]);

            if (worstMargin is null || margin < worstMargin)
            {
                worstMargin = margin;
                worstHz = frequenciesHz[i];
            }

            if (margin >= 0) { Close(); continue; }

            if (openAt < 0 || margin < openWorst)
            {
                if (openAt < 0) openAt = i;
                openWorst = margin;
                openWorstHz = frequenciesHz[i];
                openWorstZ = magnitudeOhms[i];
                openWorstLimit = lim;
            }
        }

        Close();

        // Worst first, and by FREQUENCY where two excursions are equally bad, so the order is a
        // property of the answer rather than of the grid's direction.
        rows.Sort(static (p, q) =>
        {
            int byMargin = p.MarginDb.CompareTo(q.MarginDb);
            return byMargin != 0 ? byMargin : p.FrequencyHz.CompareTo(q.FrequencyHz);
        });

        return new PdnMaskReport(
            rows, worstMargin, worstHz, judged, unjudged, indicative, true);

        void Close()
        {
            if (openAt < 0) return;
            rows.Add(new PdnMaskViolation(
                openWorstHz, openWorstZ, openWorstLimit, openWorst, indicative));
            openAt = -1;
        }
    }

    /// <summary>A frequency as a report spells it. <b>Shared</b> so a mask row, an anti-resonance
    /// row, a coincidence row and a note never disagree about what 7,100,000 Hz is called.</summary>
    public static string Hertz(double f) =>
        f >= 1e9 ? $"{f / 1e9:0.###} GHz"
        : f >= 1e6 ? $"{f / 1e6:0.###} MHz"
        : f >= 1e3 ? $"{f / 1e3:0.###} kHz"
        :            $"{f:0.###} Hz";

    /// <summary>An impedance as a report spells it, on <see cref="Hertz"/>'s reasoning.</summary>
    /// <remarks>
    /// <b>It scales UP as well as down</b>, which it did not until the |Z| map needed it. A PDN
    /// mask lives in milliohms so the upward decade never came up there; a plane pair's own
    /// impedance two decades below its first resonance is kilohms, and the map printed
    /// "2404.777 Ω" on a plate that said "2.405 kΩ" beside it — one number, two spellings, in one
    /// window (owner, 2026-09-19).
    /// </remarks>
    public static string Ohms(double r) =>
        !double.IsFinite(r) ? "(none)"
        : Math.Abs(r) >= 1e6  ? $"{r / 1e6:0.###} MΩ"
        : Math.Abs(r) >= 1e3  ? $"{r / 1e3:0.###} kΩ"
        : Math.Abs(r) >= 1    ? $"{r:0.###} Ω"
        : Math.Abs(r) >= 1e-3 ? $"{r * 1e3:0.###} mΩ"
        :                       $"{r * 1e6:0.###} µΩ";
}

/// <summary>
/// One contiguous excursion above the mask, reported at its worst point.
/// </summary>
/// <param name="FrequencyHz">Where it is worst.</param>
/// <param name="MagnitudeOhms">|Z| there.</param>
/// <param name="LimitOhms">What the mask allowed there.</param>
/// <param name="MarginDb">Negative, by construction — see <see cref="PdnMask.MarginDb"/>.</param>
/// <param name="Indicative">True where the numbers behind it include a class-default ESR (§9).</param>
public sealed record PdnMaskViolation(
    double FrequencyHz,
    double MagnitudeOhms,
    double LimitOhms,
    double MarginDb,
    bool Indicative)
{
    /// <summary>The sentence the table's own row reads, with the marking said out loud.</summary>
    public string Describe() =>
        $"{PdnMask.Hertz(FrequencyHz)}: {PdnMask.Ohms(MagnitudeOhms)} against " +
        $"{PdnMask.Ohms(LimitOhms)} — over by {-MarginDb:0.#} dB" +
        (Indicative ? " (indicative)" : "") + ".";
}

/// <summary>
/// What one port's curve came to against its own mask.
/// </summary>
/// <param name="Violations">One row per contiguous excursion, worst first.</param>
/// <param name="WorstMarginDb">The least margin anywhere in the judged band, or null where nothing
/// was judged. <b>This is the number the removal ranking moves</b> (R-rail12-6).</param>
/// <param name="WorstMarginHz">Where that was.</param>
/// <param name="JudgedPoints">How many sweep points the mask had something to say about.</param>
/// <param name="UnjudgedPoints">How many it did not — outside its band, or |Z| not a number.</param>
/// <param name="Indicative">True where the numbers behind it include a class-default ESR.</param>
/// <param name="HasMask">False where this port states no target at all, which is why
/// <see cref="Passes"/> is null rather than true.</param>
public sealed record PdnMaskReport(
    IReadOnlyList<PdnMaskViolation> Violations,
    double? WorstMarginDb,
    double? WorstMarginHz,
    int JudgedPoints,
    int UnjudgedPoints,
    bool Indicative,
    bool HasMask)
{
    /// <summary>
    /// True where every judged point is under the ceiling, false where one is not, and <b>null
    /// where there was no mask or nothing in band</b> — a port nobody stated a target for has not
    /// passed.
    /// </summary>
    public bool? Passes => !HasMask || JudgedPoints == 0 ? null : Violations.Count == 0;

    /// <summary>The sentence the results panel and the export provenance read.</summary>
    public string Describe() =>
        Passes switch
        {
            null  => "No impedance target was stated for this port, so its curve was not judged.",
            true  => $"Passes by {WorstMarginDb:0.#} dB at its worst, " +
                     $"{PdnMask.Hertz(WorstMarginHz ?? 0)}" + Marking() + Unjudged(),
            false => $"{Violations.Count} violation(s); worst {-(WorstMarginDb ?? 0):0.#} dB over at " +
                     $"{PdnMask.Hertz(WorstMarginHz ?? 0)}" + Marking() + Unjudged(),
        };

    private string Marking() => Indicative ? ", indicative" : "";

    private string Unjudged() =>
        UnjudgedPoints > 0
            ? $". {UnjudgedPoints} point(s) fall outside the mask's own band and were not judged."
            : ".";
}
