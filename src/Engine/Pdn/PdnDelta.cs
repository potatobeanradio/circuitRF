// Δ|Z| in dB versus frequency, and where it moved most — the A/B comparison's own arithmetic
// (docs/design/railrf.md §2.5; docs/sonnet-briefs/brief-railrf-16-ab-comparison.md R-rail16-4).
//
// ── WHY THE DELTA IS IN DECIBELS AND NOT IN OHMS ───────────────────────────────────────────────
//
// §2.5 asks for "Δ|Z| in dB versus frequency", and the unit is the finding rather than a formatting
// choice. A PDN curve crosses three or four orders of magnitude between the bulk's own resonance
// and the top of the band: at 30 kHz it is a couple of milliohms and at 80 MHz it is an ohm. A
// difference in OHMS is therefore almost entirely a picture of where the curve is big — 400 mΩ of
// movement at the top of the band would tower over a doubling at the bottom, and the doubling is
// the one that breaks a mask stated as a flat 2.5 mΩ. A RATIO in dB is scale free: 1 mΩ → 2 mΩ and
// 500 mΩ → 1 Ω are both +6 dB, which is what "this got twice as bad" means on either.
//
// ── THE SIGN IS FIXED AND IT MEANS SOMETHING ───────────────────────────────────────────────────
//
//     Δ(f) = 20·log10( |Z_target(f)| / |Z_reference(f)| )
//
// POSITIVE means the target is the HIGHER impedance — the re-layout made it worse. The reference is
// the denominator because §2.5's question is "the reference passes; does yours?", and a reader who
// has to remember which way round the subtraction went reads every excursion backwards.
//
// ── THE TWO CURVES MUST BE ON THE SAME GRID, AND A MISMATCH IS A REFUSAL ───────────────────────
//
// This is not pedantry about array lengths. R-rail14-4's resonance search ADDS points to the grid
// that was asked for, and it adds them where THAT board's peaks are — so two boards swept from one
// band come back on two different axes as a matter of course, and the two axes differ precisely at
// the frequencies the comparison is about. Interpolating one onto the other would invent the target
// curve's value exactly where the target curve is changing fastest, and the invented number would
// then be reported as a several-decibel excursion with a frequency beside it. So: same grid, or
// nothing, naming both. The caller's remedy is to sweep both sides on one explicit grid.
//
// ── NO DOMAIN TYPES ────────────────────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a): a curve here is two double arrays, and which
// port, which rail and which document they came from belong to the caller.

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One frequency's worth of the comparison.
/// </summary>
/// <param name="FrequencyHz">Where.</param>
/// <param name="ReferenceOhms">|Z| on the reference design.</param>
/// <param name="TargetOhms">|Z| on the design being judged.</param>
/// <param name="DeltaDb">20·log10(target/reference) — <b>positive means the target is worse</b>.
/// NaN where either side was not a positive number, which is carried rather than zeroed: a point
/// nothing could be said about must not read as a point where nothing changed.</param>
public readonly record struct PdnDeltaPoint(
    double FrequencyHz, double ReferenceOhms, double TargetOhms, double DeltaDb);

/// <summary>
/// One contiguous stretch of frequency where the two curves disagree by more than the threshold —
/// §2.5's "the frequencies where it moved most called out".
/// </summary>
/// <remarks>
/// <b>A band, not a point, and its sign does not change inside it.</b> A resonance that MOVED
/// produces a positive excursion on one side of it and a negative one on the other, and those are
/// two different findings: the target is worse here and better there. Merging them across the
/// zero crossing would report one band whose peak is in the middle, which is the one frequency in
/// the whole range where nothing happened.
/// </remarks>
/// <param name="FrequencyHz">Where the disagreement is largest inside this band.</param>
/// <param name="DeltaDb">How large, signed.</param>
/// <param name="LowHz">The first sample of the band.</param>
/// <param name="HighHz">The last.</param>
/// <param name="ReferenceOhms">|Z| on the reference at <paramref name="FrequencyHz"/>.</param>
/// <param name="TargetOhms">|Z| on the target there.</param>
public sealed record PdnDeltaBand(
    double FrequencyHz,
    double DeltaDb,
    double LowHz,
    double HighHz,
    double ReferenceOhms,
    double TargetOhms)
{
    /// <summary>The sentence the delta table's own row reads.</summary>
    public string Describe() =>
        $"{PdnMask.Hertz(FrequencyHz)}: {(DeltaDb >= 0 ? "+" : "")}{DeltaDb:0.#} dB — " +
        $"{PdnMask.Ohms(ReferenceOhms)} on the reference against {PdnMask.Ohms(TargetOhms)}" +
        (LowHz < HighHz
            ? $", over {PdnMask.Hertz(LowHz)}–{PdnMask.Hertz(HighHz)}."
            : ".");
}

/// <summary>
/// The delta trace, and the bands it moved most in. <see cref="Refusal"/> non-null means NOTHING was
/// compared — the contract every other railRF result already states.
/// </summary>
/// <param name="Refusal">Why nothing was compared, or null.</param>
/// <param name="Points">The trace, point for point on the grid both curves were swept on.</param>
/// <param name="Excursions">The bands over the threshold, <b>largest disagreement first</b>.</param>
public sealed record PdnDeltaResult(
    string? Refusal,
    IReadOnlyList<PdnDeltaPoint> Points,
    IReadOnlyList<PdnDeltaBand> Excursions)
{
    /// <summary>The threshold the excursions were found at, so a report can say what it was.</summary>
    public double ThresholdDb { get; init; } = PdnDelta.DefaultThresholdDb;

    /// <summary>
    /// The largest disagreement anywhere, in dB, ignoring sign — or 0 where the two curves are the
    /// same curve. NaN points do not contribute.
    /// </summary>
    public double MaxAbsDeltaDb =>
        Points.Select(p => Math.Abs(p.DeltaDb))
              .Where(d => !double.IsNaN(d))
              .DefaultIfEmpty(0.0)
              .Max();

    /// <summary>Where that was, or null where there is nothing to compare.</summary>
    public double? WorstHz =>
        Points.Where(p => !double.IsNaN(p.DeltaDb))
              .OrderByDescending(p => Math.Abs(p.DeltaDb))
              .Select(p => (double?)p.FrequencyHz)
              .FirstOrDefault();

    /// <summary>
    /// True where the two curves are the same curve to within
    /// <see cref="PdnDelta.EquivalenceDb"/>.
    ///
    /// <para><b>On two identical designs this is exact</b>: the same arithmetic over the same inputs
    /// produces the same doubles, every ratio is exactly 1 and every point's delta is exactly 0 dB.
    /// The threshold is there so a report can say "equivalent" about a pair that differs only below
    /// the resolution the curve is drawn at, not to paper over a difference — and it is the same
    /// 0.05 dB <see cref="PdnRemovalRanking.DisplayResolutionDb"/> uses, for the same reason: a
    /// verdict and the printed figure it is read beside must agree.</para>
    /// </summary>
    public bool Equivalent => Refusal is null && Points.Count > 0 && MaxAbsDeltaDb < PdnDelta.EquivalenceDb;

    /// <summary>The sentence a report's own delta line reads.</summary>
    public string Describe() =>
        Refusal is { } why ? why
        : Points.Count == 0 ? "Nothing was compared."
        : Equivalent
            ? "The two curves are the same curve — nothing on this port moved."
            : $"Worst {MaxAbsDeltaDb:0.#} dB at " +
              $"{PdnMask.Hertz(WorstHz ?? 0)}, over {Excursions.Count} band(s) past " +
              $"{ThresholdDb:0.#} dB.";

    internal static PdnDeltaResult Refused(string why) => new(why, [], []);
}

/// <summary>§2.5's delta trace.</summary>
public static class PdnDelta
{
    /// <summary>
    /// How far apart the curves have to be before a stretch of frequency is CALLED OUT, in dB.
    ///
    /// <para><b>1 dB, because that is roughly what a part tolerance already moves the curve by.</b>
    /// A ±20 % ceramic is ±1.9 dB of capacitive reactance on its own, so a comparison that reported
    /// every 0.3 dB of movement would fill its own short list with differences the two boards would
    /// not reliably show if they were both built. The threshold selects what to NAME; the full trace
    /// is in <see cref="PdnDeltaResult.Points"/> either way and nothing is discarded.</para>
    /// </summary>
    public const double DefaultThresholdDb = 1.0;

    /// <summary>The movement a report calls no movement. See
    /// <see cref="PdnDeltaResult.Equivalent"/>.</summary>
    public const double EquivalenceDb = PdnRemovalRanking.DisplayResolutionDb;

    /// <summary>How far apart two grid points may be and still be the same frequency, as a
    /// fraction. Two runs that were handed one grid produce bit-identical axes; this covers a grid
    /// that made the round trip through a file, and nothing wider — see this file's header for why
    /// a real mismatch is refused rather than interpolated.</summary>
    private const double GridTolerance = 1e-9;

    /// <summary>
    /// The delta trace between two curves swept on the same grid.
    /// </summary>
    /// <param name="referenceHz">The reference design's axis.</param>
    /// <param name="referenceOhms">Its |Z|.</param>
    /// <param name="targetHz">The judged design's axis. <b>Must be the same grid</b> — see this
    /// file's header.</param>
    /// <param name="targetOhms">Its |Z|.</param>
    /// <param name="thresholdDb">What counts as an excursion. See
    /// <see cref="DefaultThresholdDb"/>.</param>
    public static PdnDeltaResult Compute(
        IReadOnlyList<double> referenceHz,
        IReadOnlyList<double> referenceOhms,
        IReadOnlyList<double> targetHz,
        IReadOnlyList<double> targetOhms,
        double thresholdDb = DefaultThresholdDb)
    {
        ArgumentNullException.ThrowIfNull(referenceHz);
        ArgumentNullException.ThrowIfNull(referenceOhms);
        ArgumentNullException.ThrowIfNull(targetHz);
        ArgumentNullException.ThrowIfNull(targetOhms);

        if (referenceHz.Count != referenceOhms.Count || targetHz.Count != targetOhms.Count)
            return PdnDeltaResult.Refused(
                $"A curve has {referenceOhms.Count} magnitude(s) on a {referenceHz.Count}-point axis " +
                $"on the reference and {targetOhms.Count} on {targetHz.Count} on the target. Each " +
                "curve carries one magnitude per swept frequency.");

        if (referenceHz.Count == 0)
            return PdnDeltaResult.Refused(
                "Neither side was swept, so there is no delta to take. Run both designs first.");

        if (GridRefusal(referenceHz, targetHz) is { } grid)
            return PdnDeltaResult.Refused(grid);

        var points = new List<PdnDeltaPoint>(referenceHz.Count);

        for (int i = 0; i < referenceHz.Count; i++)
        {
            double a = referenceOhms[i];
            double b = targetOhms[i];

            // A non-positive or non-finite magnitude makes the ratio meaningless rather than
            // infinite. NaN is the honest answer and it is carried through: the excursion search
            // skips it and MaxAbsDeltaDb ignores it, so a point nothing could be said about never
            // becomes a point where nothing changed.
            double db = a > 0 && b > 0 && !double.IsNaN(a) && !double.IsNaN(b) &&
                        !double.IsInfinity(a) && !double.IsInfinity(b)
                ? 20.0 * Math.Log10(b / a)
                : double.NaN;

            points.Add(new PdnDeltaPoint(referenceHz[i], a, b, db));
        }

        return new PdnDeltaResult(null, points, Excursions(points, thresholdDb))
        {
            ThresholdDb = thresholdDb,
        };
    }

    /// <summary>
    /// Why these two axes are not the same grid, or null when they are. Public because the report
    /// that has to explain a refusal is not the one that made it, and because a caller preparing two
    /// sweeps can ask BEFORE paying for the second.
    /// </summary>
    public static string? GridRefusal(IReadOnlyList<double> referenceHz, IReadOnlyList<double> targetHz)
    {
        ArgumentNullException.ThrowIfNull(referenceHz);
        ArgumentNullException.ThrowIfNull(targetHz);

        if (referenceHz.Count != targetHz.Count)
            return $"The reference was swept at {referenceHz.Count} frequencies and the target at " +
                   $"{targetHz.Count}. A delta is taken point for point, and railRF does not " +
                   "interpolate one curve onto the other's axis — that would invent the target's " +
                   "value exactly where it is changing fastest. Sweep both on one grid.";

        for (int i = 0; i < referenceHz.Count; i++)
        {
            double a = referenceHz[i];
            double b = targetHz[i];
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));

            if (Math.Abs(a - b) <= GridTolerance * Math.Max(scale, double.Epsilon)) continue;

            return $"The two sweeps are on different grids: point {i + 1} is " +
                   $"{PdnMask.Hertz(a)} on the reference and {PdnMask.Hertz(b)} on the target. A " +
                   "delta is taken point for point. Sweep both on one grid — a resonance search " +
                   "adds points where each board's own peaks are, which is exactly where the two " +
                   "axes part company.";
        }

        return null;
    }

    /// <summary>
    /// Contiguous runs of one sign past the threshold, each reduced to its own largest point,
    /// largest first. See <see cref="PdnDeltaBand"/> for why the sign closes a band.
    /// </summary>
    private static IReadOnlyList<PdnDeltaBand> Excursions(
        IReadOnlyList<PdnDeltaPoint> points, double thresholdDb)
    {
        if (!(thresholdDb > 0)) thresholdDb = DefaultThresholdDb;

        var bands = new List<PdnDeltaBand>();

        int start = -1;
        int peak = -1;
        int sign = 0;

        for (int i = 0; i <= points.Count; i++)
        {
            double db = i < points.Count ? points[i].DeltaDb : double.NaN;
            bool over = !double.IsNaN(db) && Math.Abs(db) >= thresholdDb;
            int thisSign = over ? Math.Sign(db) : 0;

            if (over && start >= 0 && thisSign == sign)
            {
                if (Math.Abs(db) > Math.Abs(points[peak].DeltaDb)) peak = i;
                continue;
            }

            if (start >= 0)
            {
                var p = points[peak];
                bands.Add(new PdnDeltaBand(
                    p.FrequencyHz, p.DeltaDb,
                    points[start].FrequencyHz, points[i - 1].FrequencyHz,
                    p.ReferenceOhms, p.TargetOhms));
                start = peak = -1;
                sign = 0;
            }

            if (over) { start = peak = i; sign = thisSign; }
        }

        bands.Sort(static (p, q) =>
        {
            int byMagnitude = Math.Abs(q.DeltaDb).CompareTo(Math.Abs(p.DeltaDb));
            return byMagnitude != 0 ? byMagnitude : p.FrequencyHz.CompareTo(q.FrequencyHz);
        });

        return bands;
    }
}
