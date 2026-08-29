using System.Globalization;

namespace CircuitRF.Core.Matching;

/// <summary>
/// Which of Fano's two integrals a termination is bounded by (match.md §18.10).
/// </summary>
/// <remarks>
/// <b>Two classes, four terminations.</b> A shunt C and a series L are bounded by
/// <c>∫ ln(1/|Γ|) dω</c>, so what they cost is TOTAL BANDWIDTH; a series C and a shunt L are bounded
/// by <c>∫ ln(1/|Γ|) dω/ω²</c>, so what they cost is dominated by the LOWEST band edge. That single
/// difference is why the loosen hints of <see cref="MatchFanoBound.Remedies"/> can name a specific
/// edge rather than "narrow the bands".
/// </remarks>
public enum FanoWeight
{
    /// <summary>No reactance to be bounded by — a resistive end, or a spec that is not yet a spec.</summary>
    None,

    /// <summary>The <c>dω</c> integral: parallel R‖C and series R+L.</summary>
    BandWidth,

    /// <summary>The <c>dω/ω²</c> integral: series R+C and parallel R‖L.</summary>
    InverseSquare,
}

/// <summary>
/// One termination's Fano ceiling over one band set — the best a lossless network could possibly do
/// (match.md §18.10).
/// </summary>
/// <param name="End">1 or 2 — which termination this bounds.</param>
/// <param name="Weight">Which integral bounds it; <see cref="FanoWeight.None"/> for a resistive end.</param>
/// <param name="AlphaNepers">The flat in-band <c>ln(1/|Γ|)</c> the budget affords. +∞ for a resistive end.</param>
/// <param name="CeilingDb">
/// <c>−8.686·α_max</c> — quoted NEGATIVE, the way <c>MatchResponse.WorstReturnLossDb</c> quotes an
/// achieved figure, so the two are directly comparable. −∞ for a resistive end.
/// </param>
/// <param name="BandShare">Each band's fraction of the total weight, in band order. Sums to 1; empty
/// when there is no weight to share.</param>
public sealed record FanoCeiling(
    int End,
    FanoWeight Weight,
    double AlphaNepers,
    double CeilingDb,
    IReadOnlyList<double> BandShare)
{
    /// <summary>True when this end has a reactance and the bands are a spec — i.e. there IS a ceiling.</summary>
    public bool IsBounded => Weight != FanoWeight.None && double.IsFinite(CeilingDb);
}

/// <summary>
/// One thing the user could change to reach a stated target, with the value that reaches it
/// (match.md §18.10).
/// </summary>
/// <param name="Kind">"reactance", "edge", "drop" or "mirror".</param>
/// <param name="End">Which termination, for "reactance"; null otherwise.</param>
/// <param name="Band">Which band (1-based, in the design's own numbering); null for "reactance".</param>
/// <param name="Value">
/// The value the sentence names — farads or henries for "reactance", hertz for "edge", and the
/// resulting ceiling in dB for "drop" and "mirror".
/// </param>
/// <param name="Sentence">The clause the Designer joins with "; or".</param>
public sealed record FanoRemedy(string Kind, int? End, int? Band, double Value, string Sentence);

/// <summary>
/// match.md §18.10 — the Fano ceiling, the gap rise, and the closed-form loosen hints.
/// </summary>
/// <remarks>
/// <b>Everything here is a formula evaluated once, and nothing here searches.</b> The ceiling is a
/// theorem about the terminations and the bands, not a measurement of a network, so it is available
/// BEFORE any synthesis runs and it is still available when the synthesis refuses — which is exactly
/// when the user needs it. The "loosen" hints are the same formula solved for one named variable;
/// if a formula has no solution the hint is absent rather than approximated.
///
/// <para><b>It is also an invariant.</b> A synthesised ladder's worst in-band return loss can never
/// be BETTER than the ceiling over the same bands. <c>MatchFanoBoundTests</c> checks that against
/// every golden fixture in the repository, and a failure there means this class's weight class is
/// wrong for that termination kind — not that the fixture drifted.</para>
/// </remarks>
public static class MatchFanoBound
{
    /// <summary>Nepers to dB: <c>20·log10(e)</c>.</summary>
    private const double NeperToDb = 8.685889638065035;

    /// <summary>
    /// The return loss the hints are solved for, dB.
    /// </summary>
    /// <remarks>
    /// <b>A constant, not a setting.</b> −15 dB is a usable match by any ordinary standard, and every
    /// remedy being solved for the SAME number is what makes the four sentences comparable to each
    /// other — a user-editable target would produce four clauses that each answered a different
    /// question.
    /// </remarks>
    public const double HintTargetDb = -15.0;

    /// <summary>
    /// How close to the ceiling counts as being AT it, dB.
    /// </summary>
    /// <remarks>
    /// match.md §18.4 measures the Chebyshev optimum as insensitive to K by 0.1–1 dB, so a network
    /// within one dB of the ceiling has not fallen short of anything a search could recover.
    /// </remarks>
    public const double AtCeilingSlackDb = 1.0;

    /// <summary>The prototype gap rise above which the gap is genuinely excluded (match.md §18.10).</summary>
    /// <remarks>
    /// A rise of <c>r</c> puts the gap's <c>|Γ|²</c> at <c>(K + ε²r²)/(1 + ε²r²)</c>. Below r ≈ 2 the
    /// gap sits within a few dB of the passband and the design is spending budget there as though it
    /// were band — which is what a multiband spec exists to avoid.
    /// </remarks>
    public const double GapOpenRise = 2.0;

    // ── The ceiling ───────────────────────────────────────────────────────────

    /// <summary>Which integral bounds this termination.</summary>
    public static FanoWeight WeightOf(Termination termination)
    {
        ArgumentNullException.ThrowIfNull(termination);
        if (!termination.HasReactance) return FanoWeight.None;
        bool parallel = termination.Topology == TerminationTopology.Parallel;
        return termination.Kind switch
        {
            ReactanceKind.C => parallel ? FanoWeight.BandWidth : FanoWeight.InverseSquare,
            ReactanceKind.L => parallel ? FanoWeight.InverseSquare : FanoWeight.BandWidth,
            _ => FanoWeight.None,
        };
    }

    /// <summary>
    /// The weight one band contributes to its class's integral, in SI (seconds for either class).
    /// </summary>
    /// <param name="w">Which class.</param>
    /// <param name="fLo">Lower edge, Hz.</param>
    /// <param name="fHi">Upper edge, Hz.</param>
    public static double BandWeight(FanoWeight w, double fLo, double fHi) => w switch
    {
        FanoWeight.BandWidth => 2.0 * Math.PI * (fHi - fLo),
        FanoWeight.InverseSquare => (1.0 / fLo - 1.0 / fHi) / (2.0 * Math.PI),
        _ => 0.0,
    };

    /// <summary>
    /// The ceiling of ONE termination over the given bands, read at the design's <paramref name="omega0"/>.
    /// </summary>
    /// <remarks>
    /// <b>ω₀ is inert and the test says so.</b> The bound depends on the termination only through
    /// <c>R·C</c> (or <c>L/R</c>), and <c>Q·ω₀</c> reproduces exactly that for all four combinations:
    /// a parallel C has <c>Q = ω₀RC</c> so <c>π/(RC) = πω₀/Q</c>, a series C has <c>Q = 1/(ω₀RC)</c>
    /// so <c>πRC = π/(ω₀Q)</c>, and the two inductive cases follow through <c>CeqAt</c>. Reading Q
    /// rather than <c>Kind</c> and <c>Value</c> is the same licence the rest of the synthesis takes —
    /// <see cref="WeightOf"/> is the one place that looks at the kind.
    /// </remarks>
    /// <param name="t">The termination.</param>
    /// <param name="end">1 or 2 — carried through for the Designer's sentence.</param>
    /// <param name="omega0">Band centre, rad/s.</param>
    /// <param name="bands">Increasing, positive, non-overlapping spans in Hz.</param>
    public static FanoCeiling For(
        Termination t, int end, double omega0, IReadOnlyList<(double Lo, double Hi)> bands)
    {
        ArgumentNullException.ThrowIfNull(t);
        var w = WeightOf(t);
        double q = t.QAt(omega0);
        if (w == FanoWeight.None || !(q > 0.0) || !double.IsFinite(q) || !AreBands(bands))
            return Unbounded(end);

        double total = 0.0;
        var share = new double[bands.Count];
        for (int i = 0; i < bands.Count; i++)
        {
            share[i] = BandWeight(w, bands[i].Lo, bands[i].Hi);
            total += share[i];
        }
        if (!(total > 0.0) || !double.IsFinite(total)) return Unbounded(end);
        for (int i = 0; i < share.Length; i++) share[i] /= total;

        // alpha = K / total, with K the whole of the termination: pi.omega0/Q for the dOmega class
        // and pi/(omega0.Q) for the dOmega/omega^2 one.
        double k = w == FanoWeight.BandWidth ? Math.PI * omega0 / q : Math.PI / (omega0 * q);
        double alpha = k / total;
        return new FanoCeiling(end, w, alpha, -NeperToDb * alpha, share);
    }

    /// <summary>Both ends over the design's EFFECTIVE bands (match.md §18.3).</summary>
    /// <remarks>
    /// <b>The binding end is the one with the LESS NEGATIVE ceiling</b> — the smaller budget. A
    /// resistive end has no ceiling at all and never binds, which is why <see cref="Unbounded"/>
    /// reports −∞ rather than 0.
    /// </remarks>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) Of(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Pair(design, design.Bands);
    }

    /// <summary>
    /// The same, over the bands AS TYPED — before §18.3's mirror widening moved anything.
    /// </summary>
    /// <remarks>
    /// <b>The widening COST is <c>Of(design).Binding.CeilingDb − OfTypedBands(design).Binding.CeilingDb</c></b>,
    /// and on a spec whose bands are far from mirroring it is the largest single number in the whole
    /// feasibility picture — 4.3 dB on the owner's tri-band fixture. The typed bands are not a spec
    /// any network can have (§18.3), so this is a diagnostic, never a target.
    /// </remarks>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) OfTypedBands(
        MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Pair(design, TypedBands(design));
    }

    /// <summary>
    /// The same, over the single outer span — what a prototype that does NOT exclude the gaps spends.
    /// </summary>
    public static (FanoCeiling Term1, FanoCeiling Term2, FanoCeiling Binding) OfOuterSpan(
        MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var outer = design.Effective.Outer;
        return Pair(design, [outer]);
    }

    /// <summary>The bands as the user typed them, for the design's band count.</summary>
    public static IReadOnlyList<(double Lo, double Hi)> TypedBands(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return design.BandCount switch
        {
            >= 3 => [(design.F1, design.F2), (design.F3, design.F4), (design.F5, design.F6)],
            2 => [(design.F1, design.F2), (design.F3, design.F4)],
            _ => [(design.F1, design.F2)],
        };
    }

    private static (FanoCeiling, FanoCeiling, FanoCeiling) Pair(
        MatchDesign design, IReadOnlyList<(double Lo, double Hi)> bands)
    {
        double om0 = design.Omega0;
        var c1 = For(design.Term1, 1, om0, bands);
        var c2 = For(design.Term2, 2, om0, bands);
        return (c1, c2, c1.CeilingDb >= c2.CeilingDb ? c1 : c2);
    }

    private static FanoCeiling Unbounded(int end) =>
        new(end, FanoWeight.None, double.PositiveInfinity, double.NegativeInfinity, []);

    private static bool AreBands(IReadOnlyList<(double Lo, double Hi)>? bands)
    {
        if (bands is null || bands.Count == 0) return false;
        double last = 0.0;
        foreach (var (lo, hi) in bands)
        {
            if (!(lo > last) || !(hi > lo) || !double.IsFinite(hi)) return false;
            last = hi;
        }
        return true;
    }

    // ── The gap rise ──────────────────────────────────────────────────────────

    /// <summary>
    /// How far the prototype polynomial rises above its passband level in each gap, at
    /// <paramref name="order"/> — one factor per gap, in frequency order.
    /// </summary>
    /// <remarks>
    /// <b>A rise of 1 means the gap is not excluded at all.</b> The minimax polynomial on a union of
    /// intervals is levelled to 1 on the passband; if it never exceeds 1 in the gap either, it IS the
    /// single-band hull polynomial and the "multiband" design is a single wide match over the outer
    /// span, spending its whole Fano budget there. That is not a synthesis failure — at low degree
    /// there is no polynomial that does better — but nothing else on screen says it.
    ///
    /// <para>Empty for a single band, which has no gap. The two gaps of a tri-band design map to the
    /// SAME interval in <c>u</c> (they are log-mirror images of each other, §18.3), so their factors
    /// are equal by construction; both are reported because the frequency spans are not.</para>
    /// </remarks>
    /// <param name="bands">The design's effective bands.</param>
    /// <param name="order">Match points per band, 1..<see cref="MatchOrders.MaxOrder"/>.</param>
    public static IReadOnlyList<double> GapRise(EffectiveBands bands, int order)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count <= 1 || bands.Overlaps) return [];

        var intervals = bands.Intervals;
        if (intervals.Count == 0) return [];

        var p = RiseMemo.GetOrAdd(
            (order, intervals[0].Lo, intervals[0].Hi, intervals[^1].Lo, intervals[^1].Hi),
            key => MatchRemez.MinimaxScaled(
                key.Order,
                key.Lo0 == key.LoLast && key.Hi0 == key.HiLast
                    ? [(key.Lo0, key.Hi0)]
                    : [(key.Lo0, key.Hi0), (key.LoLast, key.HiLast)]));
        if (p is null) return [];

        // The gap in u is the complement of the passband inside its own hull [0, 1]: for dual-band
        // the ONE interval is [a^2, 1] and the gap is everything below it; for tri-band the middle
        // band straddles u = 0 and the gap is what lies between the two intervals. Both frequency
        // gaps of a tri-band design map onto that single u-interval, because they are log-mirror
        // images of each other.
        double gLo, gHi;
        if (intervals.Count >= 2) { gLo = intervals[0].Hi; gHi = intervals[1].Lo; }
        else { gLo = 0.0; gHi = intervals[0].Lo; }
        if (!(gHi > gLo)) return [];

        double rise = MaxAbsOn(p, gLo, gHi);
        int gaps = bands.Count - 1;
        var result = new double[gaps];
        for (int i = 0; i < gaps; i++) result[i] = rise;
        return result;
    }

    /// <summary>
    /// The dual-band prototype's gap rise in closed form: <c>cosh(n·arccosh((1+a²)/(1−a²)))</c>.
    /// </summary>
    /// <remarks>
    /// <b>Only here so the general routine can be checked against it.</b> A single-interval passband
    /// <c>[a², 1]</c> makes the exchange return the shifted Chebyshev polynomial, whose largest value
    /// below the band is at <c>u = 0</c>; ONE code path serves both band counts, and this is the
    /// witness that it does.
    /// </remarks>
    /// <param name="a">The prototype's inner band edge, <c>MatchDesign.A</c>. 0 &lt; a &lt; 1.</param>
    /// <param name="order">Degree.</param>
    public static double DualGapRise(double a, int order)
    {
        if (!(a > 0.0) || !(a < 1.0) || order < 1) return double.NaN;
        double a2 = a * a;
        return Math.Cosh(order * Math.Acosh((1.0 + a2) / (1.0 - a2)));
    }

    /// <summary>
    /// The smallest order at which EVERY gap's rise exceeds <see cref="GapOpenRise"/>, or 0 when no
    /// order this Designer offers does.
    /// </summary>
    /// <param name="bands">The design's effective bands.</param>
    public static int GapOpensAtOrder(EffectiveBands bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count <= 1) return 0;
        for (int n = 1; n <= MatchOrders.MaxOrder; n++)
        {
            var rise = GapRise(bands, n);
            if (rise.Count > 0 && rise.All(r => r > GapOpenRise)) return n;
        }
        return 0;
    }

    /// <summary>
    /// The prototype polynomial for one interval set and degree, memoised on the interval edges.
    /// </summary>
    /// <remarks>
    /// <b>The same memo the synthesis keeps, for the same reason and on the same key.</b>
    /// <see cref="GapOpensAtOrder"/> asks for every degree 1..6 on the SAME intervals every time the
    /// status strip is refreshed, and a specification edit refreshes it; the exchange is milliseconds
    /// but six of them per keystroke are not free, and none of the six moves an edge.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int Order, double Lo0, double Hi0, double LoLast, double HiLast), MatchPrototypePolynomial?>
        RiseMemo = new();

    /// <summary>Largest |p| on an interval — a dense scan, which is all a smooth degree-6 curve needs.</summary>
    private static double MaxAbsOn(MatchPrototypePolynomial p, double lo, double hi, int points = 4001)
    {
        double max = 0.0;
        for (int i = 0; i < points; i++)
        {
            double u = lo + (hi - lo) * i / (points - 1.0);
            max = Math.Max(max, Math.Abs(p.At(u)));
        }
        return max;
    }

    // ── The loosen hints ──────────────────────────────────────────────────────

    /// <summary>
    /// What the user could change to put the BINDING ceiling at <paramref name="targetDb"/>.
    /// </summary>
    /// <remarks>
    /// <b>Four closed forms, no search, at most four entries, always in this order:</b> the
    /// termination's own reactance; the dominant band's inner edge; dropping the dominant band; and
    /// un-widening the mirror. Each solves <c>α_target = −targetDb/8.686</c> for ONE variable with
    /// everything else held, and an entry whose formula has no solution — or whose solution points the
    /// unphysical way, a shunt C asked to grow — is absent rather than clamped.
    /// </remarks>
    /// <param name="design">The design.</param>
    /// <param name="targetDb">The wanted return loss, negative (e.g. <see cref="HintTargetDb"/>).</param>
    public static IReadOnlyList<FanoRemedy> Remedies(MatchDesign design, double targetDb)
    {
        ArgumentNullException.ThrowIfNull(design);
        var (_, _, binding) = Of(design);
        if (!binding.IsBounded || !(targetDb < 0.0)) return [];

        var bands = design.Bands;
        if (!AreBands(bands)) return [];

        double alphaT = -targetDb / NeperToDb;
        if (!(alphaT > 0.0)) return [];

        var term = binding.End == 1 ? design.Term1 : design.Term2;
        double om0 = design.Omega0;
        var w = binding.Weight;

        // K is the whole termination: alpha = K / (sum of band weights).
        double k = w == FanoWeight.BandWidth
            ? Math.PI * om0 / term.QAt(om0)
            : Math.PI / (om0 * term.QAt(om0));

        double totalWeight = 0.0;
        foreach (var (lo, hi) in bands) totalWeight += BandWeight(w, lo, hi);

        var list = new List<FanoRemedy>(4);
        AddReactance(list, term, binding.End, w, alphaT, totalWeight);
        AddEdge(list, design, bands, binding, w, k, alphaT);
        AddDrop(list, design, binding);
        AddMirror(list, design);
        return list;
    }

    /// <summary>Remedy 1 — the termination value that meets the target, when it lies the loose way.</summary>
    private static void AddReactance(
        List<FanoRemedy> list, Termination t, int end, FanoWeight w, double alphaT, double totalWeight)
    {
        if (!(totalWeight > 0.0)) return;

        // alpha = K/W with K = pi/(R.C), pi.R/L, pi.R.C or pi.L/R, so the target fixes K and the
        // reactance follows. The DIRECTION is the class's own: the dOmega class wants LESS reactance
        // (a smaller shunt C, a smaller series L) and the dOmega/omega^2 class wants more.
        double kTarget = alphaT * totalWeight;
        bool wantsLarger = w == FanoWeight.InverseSquare;

        double value = t.Kind switch
        {
            // Parallel C: K = pi/(R.C).      Series C: K = pi.R.C.
            ReactanceKind.C => t.Topology == TerminationTopology.Parallel
                ? Math.PI / (t.R * kTarget)
                : kTarget / (Math.PI * t.R),
            // Series L: K = pi.R/L.          Parallel L: K = pi.L/R.
            ReactanceKind.L => t.Topology == TerminationTopology.Series
                ? Math.PI * t.R / kTarget
                : kTarget * t.R / Math.PI,
            _ => double.NaN,
        };

        if (!double.IsFinite(value) || !(value > 0.0)) return;
        // The unphysical direction — a target the termination already meets — is no remedy at all.
        if (wantsLarger ? !(value > t.Value) : !(value < t.Value)) return;

        string what = t.Kind == ReactanceKind.C ? "capacitance" : "inductance";
        string unit = t.Kind == ReactanceKind.C ? "pF" : "nH";
        double scaled = t.Kind == ReactanceKind.C ? value * 1e12 : value * 1e9;
        string dir = wantsLarger ? "at or above" : "at or below";
        list.Add(new FanoRemedy(
            "reactance", end, null, value,
            $"termination {end}'s {what} {dir} {Sig3(scaled)} {unit}"));
    }

    /// <summary>Remedy 2 — the dominant band's inner edge, the other bands held.</summary>
    private static void AddEdge(
        List<FanoRemedy> list, MatchDesign design, IReadOnlyList<(double Lo, double Hi)> bands,
        FanoCeiling binding, FanoWeight w, double k, double alphaT)
    {
        int d = Dominant(binding.BandShare);
        if (d < 0) return;

        double budget = k / alphaT;                       // the total weight the target allows
        double others = 0.0;
        for (int i = 0; i < bands.Count; i++)
            if (i != d) others += BandWeight(w, bands[i].Lo, bands[i].Hi);

        double room = budget - others;
        if (!(room > 0.0)) return;

        var (lo, hi) = bands[d];
        if (w == FanoWeight.InverseSquare)
        {
            // 1/omega_lo - 1/omega_hi = room, the upper edge held: the LOW edge is the lever, because
            // this class's weight is dominated by it.
            double newLo = 1.0 / (2.0 * Math.PI * room + 1.0 / hi);
            if (!double.IsFinite(newLo) || !(newLo > lo) || !(newLo < hi)) return;
            list.Add(new FanoRemedy(
                "edge", null, d + 1, newLo,
                $"band {d + 1} starting at {Sig3(newLo / 1e9)} GHz instead of {Sig3(lo / 1e9)}"));
            return;
        }

        // The dOmega class is limited by total width, so the lever is the widest band's WIDTH,
        // narrowed symmetrically about its own centre — neither edge is privileged here.
        double width = room / (2.0 * Math.PI);
        if (!(width > 0.0) || !(width < hi - lo)) return;
        double centre = 0.5 * (lo + hi);
        double nLo = centre - 0.5 * width, nHi = centre + 0.5 * width;
        if (!(nLo > 0.0)) return;
        list.Add(new FanoRemedy(
            "edge", null, d + 1, nLo,
            $"band {d + 1} narrowed to {Sig3(nLo / 1e9)}–{Sig3(nHi / 1e9)} GHz"));
    }

    /// <summary>Remedy 3 — the ceiling the OTHER bands give, whether or not it meets the target.</summary>
    /// <remarks>
    /// <b>Stated even when it does not reach the target</b>, because the number is the answer to "how
    /// much is this band costing me" and that question is worth answering either way. The remaining
    /// bands are re-symmetrised (§18.3) before they are measured — dropping one band of three leaves a
    /// DUAL spec, and a dual spec has its own mirror rule.
    /// </remarks>
    private static void AddDrop(List<FanoRemedy> list, MatchDesign design, FanoCeiling binding)
    {
        if (design.BandCount < 2) return;
        int d = Dominant(binding.BandShare);
        if (d < 0) return;

        var typed = TypedBands(design);
        if (d >= typed.Count) return;
        var kept = new List<(double Lo, double Hi)>(typed.Count - 1);
        var keptNumbers = new List<int>(typed.Count - 1);
        for (int i = 0; i < typed.Count; i++)
            if (i != d) { kept.Add(typed[i]); keptNumbers.Add(i + 1); }
        if (kept.Count == 0) return;

        var reduced = WithBands(design, kept);
        var (_, _, remaining) = Of(reduced);
        if (!remaining.IsBounded) return;

        string names = keptNumbers.Count == 1
            ? $"band {keptNumbers[0]}"
            : $"bands {string.Join(" and ", keptNumbers)}";
        list.Add(new FanoRemedy(
            "drop", null, d + 1, remaining.CeilingDb,
            $"without band {d + 1} the ceiling over {names} is {Sig1(remaining.CeilingDb)} dB"));
    }

    /// <summary>Remedy 4 — the spec that mirrors WITHOUT widening, and what it is worth.</summary>
    /// <remarks>
    /// <b>Two candidates, and the better ceiling wins.</b> §18.3 widens rather than shrinks because
    /// shrinking would silently design to less than the user asked for — but a user who is told what
    /// the widening COSTS may prefer to give the band back, and there are exactly two ways to do it:
    /// move the low band onto the high band's image, or the high band onto the low band's. Both are
    /// offered to <c>Symmetrise</c>/<c>Symmetrise3</c> and only a spec that comes back
    /// <c>Widened = false</c> is quoted.
    /// </remarks>
    private static void AddMirror(List<FanoRemedy> list, MatchDesign design)
    {
        if (design.BandCount < 2 || !design.Effective.Widened) return;

        var candidates = new List<(int Band, List<(double Lo, double Hi)> Spec)>(2);
        if (design.BandCount >= 3)
        {
            // The middle band is kept and fixes f0^2 = f3.f4; either outer band may be moved onto the
            // other's image.
            double f0Sq = design.F3 * design.F4;
            if (!(f0Sq > 0)) return;
            candidates.Add((3, [(design.F1, design.F2), (design.F3, design.F4),
                                (f0Sq / design.F2, f0Sq / design.F1)]));
            candidates.Add((1, [(f0Sq / design.F6, f0Sq / design.F5), (design.F3, design.F4),
                                (design.F5, design.F6)]));
        }
        else
        {
            // f1.f4 = f2.f3 is one equation in four edges: hold three and the fourth follows. Both
            // solutions that SHRINK a band are offered; the two that widen one are what §18.3 already
            // did.
            double f1 = design.F1, f2 = design.F2, f3 = design.F3, f4 = design.F4;
            if (!(f1 > 0) || !(f2 > f1) || !(f3 > f2) || !(f4 > f3)) return;
            if (f4 / f3 > f2 / f1)
            {
                candidates.Add((2, [(f1, f2), (f3, f2 * f3 / f1)]));
                candidates.Add((2, [(f1, f2), (f1 * f4 / f2, f4)]));
            }
            else
            {
                candidates.Add((1, [(f2 * f3 / f4, f2), (f3, f4)]));
                candidates.Add((1, [(f1, f1 * f4 / f3), (f3, f4)]));
            }
        }

        // "Better" is MORE NEGATIVE — a ceiling of -13.8 dB permits a deeper match than one of
        // -9.6 dB, exactly as an achieved return loss of -13.8 dB is the better match.
        FanoRemedy? best = null;
        double bestDb = double.PositiveInfinity;
        foreach (var (band, spec) in candidates)
        {
            var probe = WithBands(design, spec);
            if (probe.Effective.Widened || probe.Effective.Overlaps) continue;
            var (_, _, ceiling) = Of(probe);
            if (!ceiling.IsBounded || !(ceiling.CeilingDb < bestDb)) continue;

            var moved = spec[band - 1];
            int partner = design.BandCount >= 3 ? (band == 1 ? 3 : 1) : (band == 1 ? 2 : 1);
            bestDb = ceiling.CeilingDb;
            best = new FanoRemedy(
                "mirror", null, band, ceiling.CeilingDb,
                $"band {band} as {Sig3(moved.Lo / 1e9)}–{Sig3(moved.Hi / 1e9)} GHz mirrors band "
                + $"{partner} without widening (ceiling {Sig1(ceiling.CeilingDb)} dB)");
        }
        if (best is not null) list.Add(best);
    }

    /// <summary>A copy of the design carrying a different band set — the probe every hint runs on.</summary>
    private static MatchDesign WithBands(MatchDesign design, IReadOnlyList<(double Lo, double Hi)> bands)
    {
        var d = design.Clone();
        d.BandCount = bands.Count;
        d.F1 = bands[0].Lo; d.F2 = bands[0].Hi;
        d.F3 = bands.Count > 1 ? bands[1].Lo : 0.0;
        d.F4 = bands.Count > 1 ? bands[1].Hi : 0.0;
        d.F5 = bands.Count > 2 ? bands[2].Lo : 0.0;
        d.F6 = bands.Count > 2 ? bands[2].Hi : 0.0;
        return d;
    }

    /// <summary>Index of the band carrying the largest share of the budget, or −1.</summary>
    private static int Dominant(IReadOnlyList<double> share)
    {
        int best = -1;
        double most = 0.0;
        for (int i = 0; i < share.Count; i++)
            if (share[i] > most) { most = share[i]; best = i; }
        return best;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    /// <summary>Three significant figures, never in exponential notation.</summary>
    internal static string Sig3(double v)
    {
        if (!double.IsFinite(v)) return "—";
        if (v == 0.0) return "0";
        double mag = Math.Pow(10.0, 2 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
        double r = Math.Round(v * mag) / mag;
        return r.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>One decimal place — the register a dB figure is quoted in.</summary>
    internal static string Sig1(double v) =>
        double.IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";
}
