// The A/B report — what changed, what it cost, and what it broke
// (docs/design/railrf.md §2.5, §2.1 Q3, §2.6, Q-19;
//  docs/sonnet-briefs/brief-railrf-16-ab-comparison.md R-rail16-4 … R-rail16-7).
//
// ══ THE OUTPUT IS A REPORT, NOT A PLOT WITH A TABLE UNDER IT ══════════════════════════════════
//
// §2.5's closing paragraph is the whole specification, and it is three sentences:
//
//     these three parts got worse mounting, this trace section costs 61 mV more than the
//     reference's, and the net effect is a 6 dB mask violation at 7.1 MHz — on the converter's
//     fundamental — that the reference did not have.
//
// What changed, what it cost, what it broke, with the coincidence check's own answer inside the
// third. <see cref="RailComparisonReport.Findings"/> is those sentences and it is the FIRST block on
// the page. A report that listed the numbers and left the reader to find the finding in them has not
// done the job this tool exists for — and on a redesign there are six tables, all of which look
// alarming and most of which are noise.
//
// ══ Q-19 SET THE ORDER, AND REV 3 HAD IT WRONG ════════════════════════════════════════════════
//
// The comparison weighs what the LAYOUT did to the PDN and, through that, WHICH PARTS COULD COME
// OFF THE BOARD. It is not a load-current study. So the impedance half is the point and the DC half
// is supporting evidence, which is why the sections come out in §2.5's own order:
//
//   1. both curves and both verdicts        4. both removal rankings  ← the output Q-19 puts first
//   2. the delta trace, worst bands named   5. both DC breakdowns
//   3. the per-part mounting table          6. both mode lists
//
// R-rail16-7: the DC half sits BELOW the impedance answer and is not shrunk. §2.8's correction is
// that on these boards the copper is second only to the aged cell, and §2.5's own example of the
// first surprise on a compact redesign is a DC one — 40 mm of extra 0.3 mm trace and 60 mV that
// were not in the budget.
//
// ══ IT COMPUTES NO PHYSICS AND RE-WORDS NOTHING ═══════════════════════════════════════════════
//
// Every number here was produced by a run that already happened: <see cref="PdnSweep"/> for the
// curves, the masks, the coincidences and the removal ranking, <see cref="PdnBreakdown"/> for the
// DC rows, <see cref="PdnMountingLoopExtractor"/> for the loops, <see cref="PdnModeSolver"/> for the
// modes. The only arithmetic in this file is a SUBTRACTION — and even that is
// <see cref="PdnDelta"/>'s, in src/Engine, on the same terms as every other piece of railRF's
// arithmetic.
//
// It also draws nothing. <see cref="Sections"/> is headings and lines, and
// <c>RailReportPage</c> in <c>CircuitRF.Render</c> — brief 9's one render function, which
// `Report ▸` and the headless report both call — is what turns them into a page. §11.7: there is
// one route from an overlay to a page and not two, and src/Design sits below CircuitRF.Render so
// this file could not compose one even if it wanted to.

using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// The DC half of one side, reduced to what the comparison reads.
/// </summary>
/// <remarks>
/// <b>Not a whole <see cref="RailDcResult"/></b>: the comparison needs the ranked rows and the port
/// voltages and nothing else, and a report that demanded the netlist and the <c>DataSet</c> too
/// could not be built from two boards whose correct answer was CONSTRUCTED — which is what §7 gates
/// this feature on.
/// </remarks>
/// <param name="Breakdown">§2.4's ranked per-element table.</param>
/// <param name="Ports">The port voltages and drops.</param>
public sealed record RailComparisonDc(
    IReadOnlyList<PdnBreakdownRow> Breakdown,
    IReadOnlyList<RailPortDrop> Ports)
{
    /// <summary>What one rail's solve produced.</summary>
    public static RailComparisonDc Of(RailDcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RailComparisonDc(result.Breakdown, result.Ports);
    }
}

/// <summary>
/// Everything one design brought to the comparison.
/// </summary>
/// <remarks>
/// Every field is optional and each missing one narrows the report rather than stopping it: §2.5
/// asks for both mode lists "where P2b has run", and a pair of designs with no artwork still has
/// curves, a removal ranking and typed mounting loops to compare.
/// </remarks>
public sealed class RailComparisonSide
{
    /// <summary>The frequency answer — §2.5's first four outputs all read off this.</summary>
    public PdnSweepResult? Sweep { get; init; }

    /// <summary>The DC answer. R-rail16-7's supporting evidence.</summary>
    public RailComparisonDc? Dc { get; init; }

    /// <summary>
    /// Brief 13's computed mounting loop per refdes, where there was artwork to read one from.
    ///
    /// <para><b>It fills in; it does not overrule.</b> §2.2's rule is that a computed value is a
    /// default and a typed one is the user's — so a refdes absent from this map keeps whatever
    /// <see cref="RailPart.MountingInductanceHenries"/> states, and
    /// <see cref="RailMountingComparison.Basis"/> is what says which of the two a row is.</para>
    /// </summary>
    public IReadOnlyDictionary<string, double>? ComputedMountingHenries { get; init; }

    /// <summary>Brief 15's mode list, where it has run.</summary>
    public IReadOnlyList<PdnMode> Modes { get; init; } = [];
}

/// <summary>Where a mounting loop on one row came from.</summary>
public enum RailMountingBasisKind
{
    /// <summary>Nobody typed one and nothing computed one.</summary>
    None,

    /// <summary>The document's own <see cref="RailPart.MountingInductanceHenries"/>.</summary>
    Typed,

    /// <summary>Brief 13's, out of the board's own via geometry.</summary>
    Computed,
}

/// <summary>
/// One part's mounting loop on both designs — §2.5's per-part comparison table.
/// </summary>
/// <param name="Name">What the row is called. Both refdeses where rule 3 paired them.</param>
/// <param name="By">Which of §2.5's rules paired the two rows.</param>
/// <param name="ReferenceHenries">Its loop on the reference design, or null.</param>
/// <param name="TargetHenries">Its loop on the design being judged, or null.</param>
/// <param name="Basis">Where the two numbers came from — <b>computed only where BOTH sides
/// were</b>, because a typed 0.5 nH against a computed 1.1 nH is a comparison of a guess with a
/// measurement and the row has to say so.</param>
public sealed record RailMountingComparison(
    string Name,
    RailMatchRule By,
    double? ReferenceHenries,
    double? TargetHenries,
    RailMountingBasisKind Basis)
{
    /// <summary>How much it grew, in henries. Positive means the re-layout made it worse.</summary>
    public double? DeltaHenries =>
        ReferenceHenries is { } a && TargetHenries is { } b ? b - a : null;

    /// <summary>
    /// How much it grew, as a fraction of the reference's own loop. <b>The number to sort on</b>: a
    /// part that went 0.4 → 1.1 nH and one that went 4.0 → 4.7 nH moved by the same 0.7 nH and only
    /// the first one's resonance moved anywhere a reader would notice.
    /// </summary>
    public double? GrowthFraction =>
        ReferenceHenries is { } a && a > 0 && TargetHenries is { } b ? (b - a) / a : null;

    /// <summary>
    /// True where the loop grew by more than <see cref="RailComparisonReport.MountingNoticeFraction"/>
    /// — the rows §2.5 calls "these three parts got worse mounting".
    /// </summary>
    public bool Worse =>
        GrowthFraction is { } g && g >= RailComparisonReport.MountingNoticeFraction;

    /// <summary>§2.5's own sentence for this row.</summary>
    public string Describe()
    {
        if (ReferenceHenries is not { } a || TargetHenries is not { } b)
            return $"{Name}: {Henries(ReferenceHenries)} on the reference, {Henries(TargetHenries)} " +
                   "on this design — no comparison, because one side has no mounting loop at all.";

        string basis = Basis switch
        {
            RailMountingBasisKind.Computed => "",
            RailMountingBasisKind.Typed    => " (both typed)",
            _                              => " (one side typed, the other computed from the artwork)",
        };

        return $"{Name}: {Henries(a)} on the reference, {Henries(b)} on this design — " +
               (Math.Abs(b - a) < 1e-15
                   ? "unchanged"
                   : $"{(b > a ? "+" : "−")}{Math.Abs(b - a) * 1e12:0.#} pH" +
                     (GrowthFraction is { } g ? $", {(g >= 0 ? "+" : "−")}{Math.Abs(g):P0}" : "")) +
               basis + ".";
    }

    internal static string Henries(double? h) => h is { } v ? $"{v * 1e12:0.#} pH" : "none";
}

/// <summary>R-rail16-5's reading of one part's two removal rows.</summary>
public enum RailRemovalFinding
{
    /// <summary>It earns its place on both designs. Nothing to say.</summary>
    None,

    /// <summary>
    /// <b>§2.5's first finding.</b> It earns its place on the reference and earns nothing on this
    /// design: the re-layout SHADOWED it — something else now holds the frequency it was holding.
    /// </summary>
    Shadowed,

    /// <summary><b>§2.5's second finding.</b> It earns nothing on either design, so it can come off
    /// BOTH boards.</summary>
    RemovableOnBoth,

    /// <summary>
    /// It earned nothing on the reference and earns its place here — the re-layout made it
    /// load-bearing.
    ///
    /// <para><b>Not one of §2.5's two</b>, and it is a row classification rather than one of
    /// R-rail16-6's three sentences. It is here because it falls out of the same pairwise reading
    /// and because a removal table that said nothing about the row that changed most would be read
    /// as a table where that row did not change.</para>
    /// </summary>
    EarnsOnlyHere,
}

/// <summary>
/// One part's removal ranking on both designs — §2.5's fourth output, <b>the one Q-19's answer puts
/// first</b>.
/// </summary>
/// <param name="Name">What the row is called.</param>
/// <param name="Reference">Its ranking row on the reference, or null where it is not on that board.</param>
/// <param name="Target">Its row on this design, or null.</param>
/// <param name="Finding">R-rail16-5's reading of the pair.</param>
public sealed record RailRemovalComparison(
    string Name,
    PdnRemovalRow? Reference,
    PdnRemovalRow? Target,
    RailRemovalFinding Finding)
{
    /// <summary>The row as the paired ranking prints it.</summary>
    public string Describe() => Finding switch
    {
        RailRemovalFinding.Shadowed =>
            $"{Name}: {Growth(Reference)} on the reference and {Growth(Target)} here — SHADOWED by " +
            "the re-layout. It was holding the worst margin up on the reference and something else " +
            "holds it here, so on this board it is a candidate for deletion.",

        RailRemovalFinding.RemovableOnBoth =>
            $"{Name}: {Growth(Reference)} on the reference and {Growth(Target)} here — it earns " +
            "nothing on either board and can come off BOTH.",

        RailRemovalFinding.EarnsOnlyHere =>
            $"{Name}: {Growth(Reference)} on the reference and {Growth(Target)} here — the " +
            "re-layout made it load-bearing. It was a deletion candidate on the reference and it " +
            "is not one here.",

        _ => $"{Name}: {Growth(Reference)} on the reference, {Growth(Target)} here.",
    };

    private static string Growth(PdnRemovalRow? row) =>
        row is null ? "absent"
        : double.IsNaN(row.GrowthDb) ? "not judgeable"
        : $"{row.GrowthDb:0.0} dB";
}

/// <summary>
/// One group of copper or one part on both designs' DC breakdowns — §2.5's fifth output.
/// </summary>
/// <param name="Label">What it is — "42 mm of 0.3 mm inner copper, L3".</param>
/// <param name="Reference">Its row on the reference, or null where the reference has no such group.</param>
/// <param name="Target">Its row here, or null.</param>
public sealed record RailBreakdownComparison(
    string Label,
    PdnBreakdownRow? Reference,
    PdnBreakdownRow? Target)
{
    /// <summary>How much more this group costs here than on the reference, in volts. Positive is
    /// worse.</summary>
    public double DeltaV => (Target?.DropV ?? 0.0) - (Reference?.DropV ?? 0.0);

    /// <summary>§2.5's own sentence — "this trace section costs 61 mV more than the reference's".</summary>
    public string Describe() =>
        Reference is null ? $"{Label}: {Mv(Target?.DropV)} — on this design only."
        : Target is null  ? $"{Label}: {Mv(Reference.DropV)} on the reference — absent here."
        : $"{Label}: {Mv(Reference.DropV)} on the reference, {Mv(Target.DropV)} here" +
          (Math.Abs(DeltaV) < 5e-6 ? " — unchanged." : $" — {(DeltaV > 0 ? "+" : "−")}{Mv(Math.Abs(DeltaV))}.");

    private static string Mv(double? v) => v is { } x ? $"{x * 1e3:0.###} mV" : "none";
}

/// <summary>
/// One observation port on both designs — both curves, and the delta between them.
/// </summary>
/// <param name="Name">The port, as its anchor spells it.</param>
/// <param name="FrequenciesHz">The grid both were swept on. Empty where they were not comparable.</param>
/// <param name="ReferenceOhms">|Z| on the reference. <b>Carried rather than re-read</b>, so the plot
/// draws the curve the delta was taken from.</param>
/// <param name="TargetOhms">|Z| here.</param>
/// <param name="Delta">§2.5's delta trace, with the bands it moved most in.</param>
/// <param name="ReferenceMask">The reference's verdict, or null where it stated no target.</param>
/// <param name="TargetMask">This design's verdict.</param>
/// <param name="NewCoincidences">Every coincidence on this design that violates its mask and has no
/// counterpart on the reference — the input to R-rail16-6's third sentence.</param>
public sealed record RailPortComparison(
    string Name,
    IReadOnlyList<double> FrequenciesHz,
    IReadOnlyList<double> ReferenceOhms,
    IReadOnlyList<double> TargetOhms,
    PdnDeltaResult Delta,
    PdnMaskReport? ReferenceMask,
    PdnMaskReport? TargetMask,
    IReadOnlyList<PdnCoincidenceRow> NewCoincidences)
{
    /// <summary>The two verdicts, side by side — §2.5's "the reference passes; does yours?".</summary>
    public string Describe() =>
        $"{Name}: reference — {Verdict(ReferenceMask)} · this design — {Verdict(TargetMask)}";

    private static string Verdict(PdnMaskReport? report) =>
        report is null ? "not swept" : report.Describe();
}

/// <summary>One block of the report — a heading and the lines under it.</summary>
/// <remarks>
/// <b>Deliberately the same shape as <c>RailReportSection</c></b> in CircuitRF.Render, and
/// deliberately not that type: src/Design sits below CircuitRF.Render and cannot name it. The
/// caller maps one to the other in a line, which keeps brief 9's render function the only thing that
/// knows what a page looks like (§11.7).
/// </remarks>
public sealed record RailComparisonSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// §2.5's short report. <see cref="Refusal"/> non-null means nothing was compared.
/// </summary>
public sealed record RailComparisonReport(
    string? Refusal,
    RailComparison Match,
    IReadOnlyList<string> Findings,
    IReadOnlyList<RailPortComparison> Ports,
    IReadOnlyList<RailMountingComparison> Mounting,
    IReadOnlyList<RailRemovalComparison> Removal,
    IReadOnlyList<RailBreakdownComparison> Breakdown,
    IReadOnlyList<RailComparisonSection> Sections)
{
    /// <summary>
    /// <b>R-rail23-3c — what differed in the INPUTS.</b> "C10 unmounted", at the top of the report.
    /// </summary>
    /// <remarks>
    /// <b>A comparison of two runs of one document must name what changed between them</b>, or the
    /// reader is left diffing curves to infer it. That is the whole point of comparing against the
    /// previous run: the loop is <i>save the result, change one thing, re-run, compare</i>, and the
    /// one thing that changed is the finding. It costs nothing, because the match already pairs the
    /// two documents' own part rows.
    ///
    /// <para>It is equally true of two DIFFERENT designs — a reference board with its bulk
    /// capacitor fitted against a candidate without one is exactly the same statement — so this is
    /// computed from the match rather than from anything the previous-run path supplies.</para>
    /// </remarks>
    public IReadOnlyList<string> InputChanges { get; init; } = [];

    /// <summary>
    /// How much a mounting loop has to grow before the report names the part, as a fraction of the
    /// reference's own loop.
    ///
    /// <para><b>25 %, because that is roughly where the part's own resonance moves further than its
    /// tolerance already does.</b> f₀ goes as 1/√L, so +25 % of loop is −11 % of resonance — about
    /// what a ±20 % capacitor moves on its own (§2.4's coincidence window is the same arithmetic
    /// seen from the other side). Below that the row is still in the table with its two numbers on
    /// it; the threshold only decides what the FINDING names.</para>
    /// </summary>
    public const double MountingNoticeFraction = 0.25;

    /// <summary>
    /// True where the two designs came out the same on every output this report compares.
    ///
    /// <para>This is the state §7's last gate is about: <b>two identical designs must produce an
    /// identically zero delta and a report that says so.</b> A match that silently paired the wrong
    /// things produces a non-zero delta on identical inputs, which is the whole class of defect that
    /// row exists to catch.</para>
    /// </summary>
    public bool Equivalent =>
        Refusal is null &&
        Match.Unmatched.Count == 0 &&
        Ports.Count > 0 &&
        Ports.All(p => p.Delta.Equivalent) &&
        !Mounting.Any(m => m.DeltaHenries is { } d && Math.Abs(d) > 1e-15) &&
        Removal.All(r => r.Finding is RailRemovalFinding.None or RailRemovalFinding.RemovableOnBoth) &&
        Breakdown.All(b => Math.Abs(b.DeltaV) < 5e-6);

    /// <summary>
    /// Builds §2.5's report from a match and what each side's own run produced.
    /// </summary>
    /// <param name="match">What <see cref="RailComparison.Match"/> paired.</param>
    /// <param name="reference">The design that is known to work.</param>
    /// <param name="target">The design being judged.</param>
    public static RailComparisonReport Build(
        RailComparison match, RailComparisonSide reference, RailComparisonSide target)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);

        if (match.Refusal is { } refused)
            return new RailComparisonReport(refused, match, [], [], [], [], [], []);

        var ports      = ComparePorts(match, reference, target);
        var mounting   = CompareMounting(match, reference, target);
        var removal    = CompareRemoval(match, reference, target);
        var breakdown  = CompareBreakdown(reference, target);

        var report = new RailComparisonReport(
            null, match, [], ports, mounting, removal, breakdown, [])
        {
            InputChanges = CompareInputs(match),
        };

        var findings = ThreeSentences(report);

        return report with
        {
            Findings = findings,
            Sections = Compose(report with { Findings = findings }, reference, target),
        };
    }

    // ── output 1 and 2: both curves, and the delta ───────────────────────────

    private static IReadOnlyList<RailPortComparison> ComparePorts(
        RailComparison match, RailComparisonSide reference, RailComparisonSide target)
    {
        if (reference.Sweep is not { } a || target.Sweep is not { } b) return [];

        var rows = new List<RailPortComparison>();

        // The ports pair by ANCHOR, not by index — R-rail16-3. Two designs that declare their loads
        // in a different order have the same ports, and a comparison that zipped the two lists would
        // put U1's curve against U7's and report the difference between two different ICs as the
        // effect of the re-layout.
        foreach (var pair in match.Loads)
        {
            var pa = a.Ports.FirstOrDefault(p => p.Index == pair.ReferenceIndex);
            var pb = b.Ports.FirstOrDefault(p => p.Index == pair.TargetIndex);
            if (pa is null || pb is null) continue;

            var delta = PdnDelta.Compute(
                a.FrequenciesHz, pa.MagnitudeOhms, b.FrequenciesHz, pb.MagnitudeOhms);

            rows.Add(new RailPortComparison(
                pair.Key,
                delta.Refusal is null ? a.FrequenciesHz : [],
                pa.MagnitudeOhms,
                pb.MagnitudeOhms,
                delta,
                pa.MaskReport,
                pb.MaskReport,
                NewCoincidences(pa, pb)));
        }

        return rows;
    }

    /// <summary>
    /// Every coincidence on the judged design that is over its mask and has no counterpart within
    /// the coincidence window on the reference — "that the reference did not have".
    /// </summary>
    private static IReadOnlyList<PdnCoincidenceRow> NewCoincidences(
        PdnPortImpedance reference, PdnPortImpedance target) =>
        [.. target.Coincidences
                  .Where(t => t.Peak.MarginDb is { } m && m < 0)
                  .Where(t => !reference.Coincidences.Any(
                      r => r.Peak.MarginDb is { } m && m < 0 &&
                           string.Equals(r.AggressorName, t.AggressorName, StringComparison.OrdinalIgnoreCase) &&
                           r.Harmonic == t.Harmonic))];

    // ── output 3: the per-part mounting table ────────────────────────────────

    private static IReadOnlyList<RailMountingComparison> CompareMounting(
        RailComparison match, RailComparisonSide reference, RailComparisonSide target)
    {
        var rows = new List<RailMountingComparison>();

        foreach (var pair in match.Parts)
        {
            var (a, basisA) = Loop(pair.Reference, reference.ComputedMountingHenries);
            var (b, basisB) = Loop(pair.Target, target.ComputedMountingHenries);

            rows.Add(new RailMountingComparison(
                pair.Name, pair.By, a, b,
                basisA == basisB ? basisA : RailMountingBasisKind.None));
        }

        // Worst growth first — that is the order §2.5's own reading takes ("these three parts got
        // worse mounting"), and the rows nobody has to act on collect at the bottom.
        rows.Sort(static (p, q) =>
        {
            int byGrowth = Rank(q).CompareTo(Rank(p));
            return byGrowth != 0 ? byGrowth : string.CompareOrdinal(p.Name, q.Name);
        });

        return rows;

        static double Rank(RailMountingComparison row) =>
            row.GrowthFraction is { } g && !double.IsNaN(g) ? g : double.NegativeInfinity;

        static (double?, RailMountingBasisKind) Loop(
            RailPart part, IReadOnlyDictionary<string, double>? computed)
        {
            // §2.2's precedence, and it is the same one RailPartResolver applies: a typed number is
            // the user's answer and a computed one fills in where they typed none.
            if (part.MountingInductanceHenries is { } typed)
                return (typed, RailMountingBasisKind.Typed);

            if (computed is not null && part.Refdes.Length > 0 &&
                computed.TryGetValue(part.Refdes, out double henries))
                return (henries, RailMountingBasisKind.Computed);

            return (null, RailMountingBasisKind.None);
        }
    }

    // ── output 4: both removal rankings, read as a PAIR (R-rail16-5) ─────────

    private static IReadOnlyList<RailRemovalComparison> CompareRemoval(
        RailComparison match, RailComparisonSide reference, RailComparisonSide target)
    {
        var left  = (reference.Sweep?.Removal ?? []).ToList();
        var right = (target.Sweep?.Removal ?? []).ToList();

        if (left.Count == 0 && right.Count == 0) return [];

        var rows  = new List<RailRemovalComparison>();
        // Reference identity, not structural: two rows can carry the same name and the same numbers
        // on the two designs — which is exactly what the identical-pair case produces — and a
        // structural set would drop the second of them.
        var taken = new HashSet<PdnRemovalRow>(ReferenceEqualityComparer.Instance);

        foreach (var pair in match.Parts)
        {
            var ra = Find(left, pair.Reference.Refdes);
            var rb = Find(right, pair.Target.Refdes);
            if (ra is null && rb is null) continue;

            if (ra is not null) taken.Add(ra);
            if (rb is not null) taken.Add(rb);
            rows.Add(new RailRemovalComparison(pair.Name, ra, rb, Read(ra, rb)));
        }

        // A part on one design only still gets a row: it is precisely the "one part deleted" case,
        // and a ranking that simply omitted it would read as a ranking where it earned nothing.
        foreach (var row in left)
            if (taken.Add(row)) rows.Add(new RailRemovalComparison(row.Name, row, null, Read(row, null)));

        foreach (var row in right)
            if (taken.Add(row)) rows.Add(new RailRemovalComparison(row.Name, null, row, Read(null, row)));

        rows.Sort(static (p, q) =>
        {
            int byFinding = Weight(q.Finding).CompareTo(Weight(p.Finding));
            if (byFinding != 0) return byFinding;
            int byGrowth = Growth(q).CompareTo(Growth(p));
            return byGrowth != 0 ? byGrowth : string.CompareOrdinal(p.Name, q.Name);
        });

        return rows;

        // The ranking names a part the way RailPartResolver names one — the refdes with the part
        // number in brackets after it, because the ranking is read on its own and "C3" alone would
        // not say which part C3 is. The COMPARISON keys on the refdes, because that is §2.5's rule 2
        // and because the two designs' part numbers may legitimately differ for one refdes (a
        // second-source part is still that part). So the lookup goes FROM the refdes the match
        // already holds, rather than parsing the row's name apart.
        static PdnRemovalRow? Find(List<PdnRemovalRow> rows, string refdes)
        {
            if (refdes.Length == 0) return null;

            foreach (var row in rows)
                if (string.Equals(row.Name, refdes, StringComparison.OrdinalIgnoreCase) ||
                    (row.Name.Length > refdes.Length &&
                     row.Name.StartsWith(refdes, StringComparison.OrdinalIgnoreCase) &&
                     row.Name[refdes.Length] == ' '))
                    return row;

            return null;
        }

        static RailRemovalFinding Read(PdnRemovalRow? a, PdnRemovalRow? b) =>
            (a, b) switch
            {
                ({ Redundant: false }, { Redundant: true })  => RailRemovalFinding.Shadowed,
                ({ Redundant: true }, { Redundant: true })   => RailRemovalFinding.RemovableOnBoth,
                ({ Redundant: true }, { Redundant: false })  => RailRemovalFinding.EarnsOnlyHere,
                _                                            => RailRemovalFinding.None,
            };

        static int Weight(RailRemovalFinding f) => f switch
        {
            RailRemovalFinding.Shadowed        => 3,
            RailRemovalFinding.EarnsOnlyHere   => 2,
            RailRemovalFinding.RemovableOnBoth => 1,
            _                                  => 0,
        };

        static double Growth(RailRemovalComparison row)
        {
            double a = row.Reference?.GrowthDb ?? 0;
            double b = row.Target?.GrowthDb ?? 0;
            if (double.IsNaN(a)) a = 0;
            if (double.IsNaN(b)) b = 0;
            return Math.Max(a, b);
        }
    }

    // ── output 5: both DC breakdowns (R-rail16-7) ────────────────────────────

    private static IReadOnlyList<RailBreakdownComparison> CompareBreakdown(
        RailComparisonSide reference, RailComparisonSide target)
    {
        if (reference.Dc is not { } a || target.Dc is not { } b) return [];

        // Rows pair on the GROUP KEY, which is what the extractor made one row out of — the trace
        // section, the via group, the part. The label is prose and moves with the geometry ("42 mm"
        // becomes "82 mm"), so pairing on it would report one changed section as two absences.
        var right = b.Breakdown
                     .GroupBy(r => Key(r), StringComparer.Ordinal)
                     .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<RailBreakdownComparison>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in a.Breakdown)
        {
            string key = Key(row);
            seen.Add(key);
            rows.Add(new RailBreakdownComparison(
                row.Label, row, right.TryGetValue(key, out var mate) ? mate : null));
        }

        foreach (var row in b.Breakdown)
            if (seen.Add(Key(row)))
                rows.Add(new RailBreakdownComparison(row.Label, null, row));

        // Ranked by what CHANGED, not by what is biggest: §2.5's DC finding is "60 mV that were not
        // in the budget", and the largest row on both boards is usually the same largest row.
        rows.Sort(static (p, q) =>
        {
            int byDelta = Math.Abs(q.DeltaV).CompareTo(Math.Abs(p.DeltaV));
            return byDelta != 0 ? byDelta : string.CompareOrdinal(p.Label, q.Label);
        });

        return rows;

        static string Key(PdnBreakdownRow row) => row.GroupKey.Length > 0 ? row.GroupKey : row.Label;
    }

    // ── R-rail23-3c: what changed in the INPUTS ──────────────────────────────

    /// <summary>
    /// The differences between the two sides' own part rows that nothing else on the page reports:
    /// what is fitted, and what part it is.
    /// </summary>
    /// <remarks>
    /// <b>Rows only, never results.</b> Everything else on this report is a difference between two
    /// ANSWERS; this is the difference between two QUESTIONS, which is why it is computed from
    /// <see cref="RailComparison.Parts"/> alone and needs neither side's run.
    /// </remarks>
    private static IReadOnlyList<string> CompareInputs(RailComparison match)
    {
        var lines = new List<string>();

        foreach (var pair in match.Parts)
        {
            if (pair.Reference.Mounted && !pair.Target.Mounted)
                lines.Add($"{pair.Name} unmounted — it is fitted on " +
                          $"{match.ReferenceName} and is not in this answer.");
            else if (!pair.Reference.Mounted && pair.Target.Mounted)
                lines.Add($"{pair.Name} mounted — it is not fitted on " +
                          $"{match.ReferenceName} and is in this answer.");
        }

        foreach (var pair in match.Parts)
            if (!string.Equals(pair.Reference.PartNumber, pair.Target.PartNumber,
                               StringComparison.OrdinalIgnoreCase))
                lines.Add($"{pair.Name} is a different part: " +
                          $"{Spell(pair.Reference.PartNumber)} → {Spell(pair.Target.PartNumber)}.");

        // A CHANGED MOUNTING LOOP IS DELIBERATELY NOT HERE. It is already the subject of the
        // per-part table and of the first of R-rail16-6's three sentences — "these three parts
        // got worse mounting" — so naming it again at the top would be the same finding twice,
        // and this block is for the inputs nothing else on the page reports.

        return lines;

        static string Spell(string partNumber) =>
            partNumber is { Length: > 0 } p ? $"'{p}'" : "(no part number)";
    }

    // ── R-rail16-6: what changed, what it cost, what it broke ────────────────

    private static IReadOnlyList<string> ThreeSentences(RailComparisonReport report)
    {
        var lines = new List<string>();

        // FIRST, and before the equivalence shortcut below (R-rail23-3c). What changed in the
        // inputs is the finding when the two sides are two runs of one document, and a report that
        // led with "the two designs are equivalent" over a board one capacitor lighter would be
        // saying the one thing the reader must not conclude.
        lines.AddRange(report.InputChanges);

        if (report.Equivalent)
        {
            lines.Add("The two designs are equivalent: every port's curve is the same curve, every " +
                      "mounting loop is the same loop, every DC row costs the same, and both " +
                      "removal rankings agree. The re-layout changed nothing this rail can see.");
            return lines;
        }

        lines.Add(WhatChanged(report));
        lines.Add(WhatItCost(report));
        lines.Add(WhatItBroke(report));

        foreach (var pair in report.Match.CoordinateAnchored)
            lines.Add(pair.CoordinateCaveat());

        return lines;
    }

    /// <summary>Sentence 1 — "these three parts got worse mounting".</summary>
    private static string WhatChanged(RailComparisonReport report)
    {
        var worse = report.Mounting.Where(m => m.Worse).ToList();

        if (worse.Count == 0)
            return report.Mounting.Count == 0
                ? "No part could be compared on both designs, so railRF has nothing to say about " +
                  "what the re-layout did to the mounting loops."
                : $"No part's mounting loop grew by more than {MountingNoticeFraction:P0}: the " +
                  "re-layout left the decoupling mounted about as well as the reference has it.";

        string named = string.Join(", ", worse.Take(3).Select(
            m => $"{m.Name} ({RailMountingComparison.Henries(m.ReferenceHenries)} → " +
                 $"{RailMountingComparison.Henries(m.TargetHenries)})"));

        return worse.Count switch
        {
            1 => $"One part got worse mounting: {named}.",
            _ => $"{worse.Count} parts got worse mounting: {named}" +
                 (worse.Count > 3 ? $", and {worse.Count - 3} more." : "."),
        };
    }

    /// <summary>Sentence 2 — "this trace section costs 61 mV more than the reference's".</summary>
    private static string WhatItCost(RailComparisonReport report)
    {
        if (report.Breakdown.Count == 0)
            return "Neither design's DC answer was supplied, so railRF cannot say what the " +
                   "re-layout costs in millivolts.";

        var worst = report.Breakdown[0];
        double total = report.Breakdown.Sum(r => r.DeltaV);

        if (Math.Abs(worst.DeltaV) < 5e-6)
            return "No row of the DC breakdown moved: the re-layout costs the same copper it did " +
                   "on the reference.";

        return $"{worst.Label} costs {Math.Abs(worst.DeltaV) * 1e3:0.###} mV " +
               $"{(worst.DeltaV > 0 ? "more" : "less")} than the reference's" +
               (Math.Abs(total - worst.DeltaV) > 5e-6
                   ? $", and the whole rail {(total > 0 ? "loses" : "gains")} " +
                     $"{Math.Abs(total) * 1e3:0.###} mV against it."
                   : ".");
    }

    /// <summary>
    /// Sentence 3 — "a 6 dB mask violation at 7.1 MHz, on the converter's fundamental, that the
    /// reference did not have".
    ///
    /// <para><b>The coincidence check's own answer is INSIDE this sentence</b> (R-rail16-6). §2.2's
    /// rule is that a 9 dB peak nothing excites is not a problem and a 3 dB peak on the converter's
    /// fifth harmonic is — so a report that named the violation and left the aggressor to a table
    /// further down would rank its own finding wrong.</para>
    /// </summary>
    private static string WhatItBroke(RailComparisonReport report)
    {
        if (report.Ports.Count == 0)
            return "Neither design's frequency answer was supplied, so railRF cannot say whether " +
                   "the re-layout broke anything.";

        var broken = report.Ports
            .Where(p => p.TargetMask?.Passes == false)
            .OrderBy(p => p.TargetMask!.WorstMarginDb ?? 0)
            .ToList();

        if (broken.Count == 0)
        {
            var moved = report.Ports.OrderByDescending(p => p.Delta.MaxAbsDeltaDb).First();
            return moved.Delta.Equivalent
                ? "Nothing broke: every port's curve is the same curve the reference has."
                : $"Nothing broke — every port still meets its target — though {moved.Name} moved " +
                  $"by {moved.Delta.MaxAbsDeltaDb:0.#} dB at " +
                  $"{PdnMask.Hertz(moved.Delta.WorstHz ?? 0)}.";
        }

        var port = broken[0];
        var report0 = port.TargetMask!;
        double over = -(report0.WorstMarginDb ?? 0);
        double hz = report0.WorstMarginHz ?? 0;

        string aggressor = port.NewCoincidences.Count > 0
            ? $" — {Aggressor(port.NewCoincidences[0])} —"
            : "";

        string wasItThere = port.ReferenceMask?.Passes switch
        {
            true  => "that the reference did not have.",
            false => $"against the reference's own worst of " +
                     $"{-(port.ReferenceMask!.WorstMarginDb ?? 0):0.#} dB over.",
            _     => "on a port the reference states no target for.",
        };

        return $"The net effect is a {over:0.#} dB mask violation at {PdnMask.Hertz(hz)} on " +
               $"{port.Name}{aggressor} {wasItThere}";
    }

    private static string Aggressor(PdnCoincidenceRow row) =>
        row.Harmonic == 1
            ? $"on the {row.AggressorName}'s own fundamental"
            : $"on the {row.AggressorName}'s harmonic {row.Harmonic}, {PdnMask.Hertz(row.AggressorHz)}";

    // ── the page's blocks, in §2.5's own order (R-rail16-4) ──────────────────

    private static IReadOnlyList<RailComparisonSection> Compose(
        RailComparisonReport report, RailComparisonSide reference, RailComparisonSide target)
    {
        var sections = new List<RailComparisonSection>
        {
            // First, always. See this file's header.
            new("What this comparison found", report.Findings),
        };

        string a = report.Match.ReferenceName;
        string b = report.Match.TargetName;

        if (report.Ports.Count > 0)
        {
            sections.Add(new RailComparisonSection(
                "Impedance — both curves against the mask",
                [.. report.Ports.Select(p => p.Describe())]));

            sections.Add(new RailComparisonSection(
                "Δ|Z| — where the two curves part company",
                [.. report.Ports.SelectMany(p =>
                     p.Delta.Refusal is { } why
                         ? [$"{p.Name}: {why}"]
                         : p.Delta.Excursions.Count == 0
                             ? new[] { $"{p.Name}: {p.Delta.Describe()}" }
                             : [.. p.Delta.Excursions.Take(6).Select(e => $"{p.Name} · {e.Describe()}")])]));
        }

        if (report.Mounting.Count > 0)
            sections.Add(new RailComparisonSection(
                "Mounting inductance, part by part",
                [.. report.Mounting.Select(m => m.Describe())]));

        if (report.Removal.Count > 0)
            sections.Add(new RailComparisonSection(
                "Which parts earn their place — both rankings",
                [.. report.Removal.Select(r => r.Describe())]));

        // R-rail16-7: below the impedance answer, and not shrunk.
        if (report.Breakdown.Count > 0)
            sections.Add(new RailComparisonSection(
                "Where the DC drop is — both breakdowns",
                [.. report.Breakdown.Select(r => r.Describe())]));

        if (reference.Modes.Count > 0 || target.Modes.Count > 0)
            sections.Add(new RailComparisonSection(
                "Plane modes",
                [$"{a}: {Modes(reference.Modes)}", $"{b}: {Modes(target.Modes)}"]));

        // Last, and never omitted: §2.5's rule is that railRF says what it could not match and asks.
        if (report.Match.Unmatched.Count > 0)
            sections.Add(new RailComparisonSection(
                "What railRF could not match",
                [.. report.Match.Unmatched.Select(u => u.Describe())]));

        if (report.Match.Notes.Count > 0)
            sections.Add(new RailComparisonSection("Notes", report.Match.Notes));

        return sections;

        static string Modes(IReadOnlyList<PdnMode> modes) =>
            modes.Count == 0
                ? "no mode list — the cavity solve has not run on this design"
                : string.Join(", ", modes.Take(6).Select(m => PdnMask.Hertz(m.FrequencyHz)));
    }
}
