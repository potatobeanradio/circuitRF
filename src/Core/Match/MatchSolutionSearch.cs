using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Core.Matching;

/// <summary>One complete, buildable answer: a set of transforms whose N's reach the far termination.</summary>
public sealed class MatchSolution
{
    /// <summary>The transforms, in application order, with the N's the search settled on.</summary>
    public required IReadOnlyList<TransformRecord> Transforms { get; init; }

    /// <summary>The analysis-end Q this solution was synthesised at, or 0 for the design's own.</summary>
    public double QAdjust { get; init; }

    /// <summary>A stable identity, so MN-3 can mark "current" and "previously applied".</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The finished network, with the §4.5/§4.6 end splits applied.</summary>
    public required MatchNetwork Network { get; init; }

    /// <summary>The product of N^2 reached.</summary>
    public double Achieved { get; init; }

    /// <summary>The product of N^2 required.</summary>
    public double Required { get; init; }

    /// <summary>Worst in-band |S11|, dB.</summary>
    public double WorstReturnLossDb { get; init; }

    /// <summary>Where each transform's pair sat in the basis ladder — the sort key of match.md §4.8.</summary>
    public IReadOnlyList<(int A, int B)> PairPositions { get; init; } = [];

    /// <summary>
    /// True when some element came out above 1 H / 1 F or below 1e-24 — exact, response-preserving
    /// and unbuildable. The solution is still offered; MN-3 says so rather than hiding it.
    /// </summary>
    public bool ImplausibleValues { get; init; }
}

/// <summary>Every solution the search found, or why there are none.</summary>
public sealed class MatchSolutionSet
{
    /// <summary>The basis synthesis.</summary>
    public required MatchSynthesisResult Basis { get; init; }

    /// <summary>Ranked: fewest transforms first, then by pair position, then by Q-adjust.</summary>
    public IReadOnlyList<MatchSolution> Solutions { get; init; } = [];

    /// <summary>Non-null when there is nothing to offer.</summary>
    public MatchRefusal? Refusal { get; init; }

    /// <summary>Anything the caller should be told but that is not a refusal.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// match.md §4.7/§4.8: enumerate non-conflicting transform sets, drive each set's N's until
/// <c>product(N^2)</c> reaches <c>R_far_target / R_far_synthesised</c>, and rank what survives.
/// </summary>
public static class MatchSolutionSearch
{
    /// <summary>The default floor on a Q-adjusted analysis-end Q (match.md §4.6's <c>Qmin</c> setting).</summary>
    public const double DefaultQMin = 2.0;

    /// <summary>
    /// <b>The tolerance is RELATIVE.</b> The reference implementation compares the achieved and
    /// required transform ratios with an absolute 1e3*epsilon (about 2.2e-13), which is meaningless
    /// against a required ratio of ~119 — it is 1e-15 of the quantity being tested.
    ///
    /// <para>It is <see cref="MatchLinkage.RatioTolerance"/>'s value, not a second opinion: the
    /// status strip, the linkage and this search must agree about what "reached" means, or a solution
    /// this list offers can be reported as not reached the moment it is applied.</para>
    /// </summary>
    public const double RatioTolerance = MatchLinkage.RatioTolerance;

    /// <summary>Searches a design for buildable solutions.</summary>
    /// <param name="design">The design. Its own <see cref="MatchDesign.Transforms"/> are not consulted.</param>
    /// <param name="includeQAdjust">Offer match.md §4.6's Q-adjusted extra solution.</param>
    /// <param name="qMin">The floor on a Q-adjusted Q.</param>
    public static MatchSolutionSet Search(
        MatchDesign design, bool includeQAdjust = true, double qMin = DefaultQMin)
    {
        ArgumentNullException.ThrowIfNull(design);

        var basis = MatchSynthesis.Synthesize(design);
        if (!basis.Ok)
            return new MatchSolutionSet { Basis = basis, Refusal = basis.Refusal };

        var notes = new List<string>(basis.Notes);
        var solutions = new List<MatchSolution>(SolutionsFor(design, basis, design.QAdjust));

        if (includeQAdjust && design.QAdjust <= 0)
        {
            var q = FindQAdjust(design, qMin);
            if (q is { } qa)
            {
                var adjusted = design.Clone();
                adjusted.QAdjust = qa;
                var adjustedBasis = MatchSynthesis.Synthesize(adjusted);
                if (adjustedBasis.Ok)
                {
                    var extra = SolutionsFor(adjusted, adjustedBasis, qa).ToList();
                    if (extra.Count > 0)
                    {
                        solutions.AddRange(extra);
                        notes.Add(
                            $"A Q-adjusted analysis end at Q = {qa:0.###} (its own Q is " +
                            $"{basis.QAnalysisActual:0.###}) also completes; it adds one element at " +
                            "that end and generally needs a lower order.");
                    }
                }
            }
        }

        if (solutions.Count == 0)
        {
            var pairs = NortonTransform.Discover(basis.Network!);
            var refusal = pairs.Count == 0
                ? MatchRefusal.Create(
                    MatchRefusalKind.NoTransformablePairs,
                    $"The order-{design.Order} ladder as synthesised has no transformable pair: no two " +
                    "like-kind elements of opposite orientation sit within three positions of each " +
                    "other with neither of them absorbed.",
                    null,
                    ("elements", basis.Network!.Elements.Count),
                    ("required", basis.RequiredTransformRatio))
                : MatchRefusal.Create(
                    MatchRefusalKind.TransformsCannotReachTarget,
                    $"No combination of the {pairs.Count} available transforms reaches the required " +
                    $"ratio of {basis.RequiredTransformRatio:0.###} inside their positivity ranges " +
                    $"({BestReach(design, basis):0.###} was the closest). Allow negative components, " +
                    "change the order, or change the response.",
                    null,
                    ("required", basis.RequiredTransformRatio),
                    ("bestAchieved", BestReach(design, basis)),
                    ("pairs", pairs.Count));
            return new MatchSolutionSet { Basis = basis, Refusal = refusal, Notes = notes };
        }

        solutions.Sort(Compare);
        return new MatchSolutionSet { Basis = basis, Solutions = solutions, Notes = notes };
    }

    private static int Compare(MatchSolution a, MatchSolution b)
    {
        int c = a.Transforms.Count.CompareTo(b.Transforms.Count);
        if (c != 0) return c;
        c = First(a).CompareTo(First(b));
        if (c != 0) return c;
        c = Second(a).CompareTo(Second(b));
        if (c != 0) return c;
        return a.QAdjust.CompareTo(b.QAdjust);

        static int First(MatchSolution s) => s.PairPositions.Count > 0 ? s.PairPositions[0].A : int.MaxValue;
        static int Second(MatchSolution s) => s.PairPositions.Count > 0 ? s.PairPositions[0].B : int.MaxValue;
    }

    /// <summary>
    /// match.md §4.7's candidate-set enumeration: each pair alone, and each pair extended by every
    /// later pair that conflicts with nothing already in the running set.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> EnumerateSets(IReadOnlyList<TransformPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sets = new List<IReadOnlyList<int>>();

        void Emit(IEnumerable<int> indices)
        {
            var list = indices.Order().ToList();
            if (list.Count > 0 && seen.Add(string.Join(",", list))) sets.Add(list);
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            Emit([i]);
            var running = new List<int> { i };
            for (int k = i + 1; k < pairs.Count; k++)
            {
                if (running.Any(r => NortonTransform.Conflicts(pairs[r], pairs[k]))) continue;
                running.Add(k);
                Emit(running);
                Emit([i, k]);
            }
        }
        return sets;
    }

    private static IEnumerable<MatchSolution> SolutionsFor(
        MatchDesign design, MatchSynthesisResult basis, double qAdjust)
    {
        var pairs = NortonTransform.Discover(basis.Network!);
        double required = basis.RequiredTransformRatio;
        if (!double.IsFinite(required) || required <= 0) yield break;

        foreach (var set in EnumerateSets(pairs))
        {
            var solved = Solve(design, basis, pairs, set, required, qAdjust);
            if (solved is not null) yield return solved;
        }
    }

    private static double BestReach(MatchDesign design, MatchSynthesisResult basis)
    {
        var pairs = NortonTransform.Discover(basis.Network!);
        double required = basis.RequiredTransformRatio;
        double best = 1.0;
        foreach (var set in EnumerateSets(pairs))
        {
            var run = Drive(design, basis, pairs, set, required);
            if (Math.Abs(Math.Log(run.Achieved / required)) < Math.Abs(Math.Log(best / required)))
                best = run.Achieved;
        }
        return best;
    }

    private static MatchSolution? Solve(
        MatchDesign design, MatchSynthesisResult basis,
        IReadOnlyList<TransformPair> pairs, IReadOnlyList<int> set, double required, double qAdjust)
    {
        var run = Drive(design, basis, pairs, set, required);
        if (run.Network is null) return null;
        if (Math.Abs(run.Achieved / required - 1.0) > RatioTolerance) return null;
        if (!design.AllowNegativeComponents && run.Network.Elements.Any(e => e.Value <= 0)) return null;

        var network = MatchSynthesis.WithEndSplits(run.Network, basis, design);
        return new MatchSolution
        {
            Transforms = run.Records,
            QAdjust = qAdjust,
            Fingerprint = SolutionFingerprint(design, run.Records),
            Network = network,
            Achieved = run.Achieved,
            Required = required,
            WorstReturnLossDb = MatchResponse.WorstReturnLossDb(network, design.F1, design.F2),
            ImplausibleValues = run.GuardFired,
            PairPositions = [.. set.Select(i => (pairs[i].IndexA, pairs[i].IndexB))],
        };
    }

    private sealed record DriveResult(
        MatchNetwork? Network, IReadOnlyList<TransformRecord> Records, double Achieved, bool GuardFired);

    /// <summary>
    /// Seeds every N at its equal geometric share of the required product and then drives one index
    /// at a time toward that product, recomputing every range at each application.
    /// </summary>
    /// <remarks>
    /// <b>The seed is the equal share, not 1.</b> The brief starts every N at 1 and then corrects
    /// index by index; the first index then asks for the WHOLE ratio at once — sqrt(119.027) = 10.91
    /// on the design doc's own problem — is clamped onto its positivity threshold of 5.989, and one
    /// of the three pi/T products goes to 2.9 kH one part in 1e9 from its pole. Every later index
    /// then only mops up. The equal share lands the same two-transform set on N = 3.303 each, well
    /// inside both ranges, with the same product and the same response. Correcting index by index
    /// after the seed is unchanged, and is what handles a set whose ranges cannot take equal shares.
    /// </remarks>
    private static DriveResult Drive(
        MatchDesign design, MatchSynthesisResult basis,
        IReadOnlyList<TransformPair> pairs, IReadOnlyList<int> set, double required)
    {
        int k = set.Count;
        var n = new double[k];
        Array.Fill(n, Math.Pow(required, 1.0 / (2.0 * k)));

        var run = Run(design, basis, pairs, set, n);
        if (run.Network is null) return run;

        for (int pass = 0; pass < 4; pass++)
        {
            if (Math.Abs(run.Achieved / required - 1.0) <= RatioTolerance) break;
            for (int idx = 0; idx < k; idx++)
            {
                for (int iter = 0; iter < 30; iter++)
                {
                    if (Math.Abs(run.Achieved / required - 1.0) <= RatioTolerance) break;
                    double candidate = n[idx] * Math.Sqrt(required / run.Achieved);
                    if (!double.IsFinite(candidate) || candidate <= 0) break;
                    var trial = Run(design, basis, pairs, set, Replace(n, idx, candidate));
                    if (trial.Network is null) break;
                    double moved = Math.Abs(trial.Records[idx].N - run.Records[idx].N);
                    n = [.. trial.Records.Select(r => r.N)];
                    run = trial;
                    if (moved <= 1e-15 * Math.Max(1.0, Math.Abs(n[idx]))) break;
                }
            }
        }
        return run;
    }

    private static double[] Replace(double[] source, int index, double value)
    {
        var copy = (double[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private static DriveResult Run(
        MatchDesign design, MatchSynthesisResult basis,
        IReadOnlyList<TransformPair> pairs, IReadOnlyList<int> set, double[] n)
    {
        var records = new List<TransformRecord>(set.Count);
        for (int i = 0; i < set.Count; i++)
            records.Add(new TransformRecord(
                pairs[set[i]].NameA, pairs[set[i]].NameB, TransformForm.Pi, n[i], Locked: false));

        var seq = MatchRebuild.ApplySequence(basis, records, design.AllowNegativeComponents);
        if (seq.Dropped.Count > 0) return new DriveResult(null, records, 1.0, seq.GuardFired);
        return new DriveResult(
            seq.Network, [.. seq.Applied.Select(a => a.Record)], seq.Achieved, seq.GuardFired);
    }

    /// <summary>
    /// match.md §4.6: bisect on the analysis end's equivalent capacitance for the smallest deliberate
    /// Q inflation at which some transform set completes.
    /// </summary>
    /// <remarks>
    /// The brief labels both bracket moves "toward MORE detune"; the operations it gives
    /// (series: <c>lo = guess</c>, parallel: <c>hi = guess</c>) both narrow toward LESS detune when a
    /// guess succeeds, which is what finding the MINIMUM such Q requires. The operations are what is
    /// implemented; the label is not.
    /// </remarks>
    public static double? FindQAdjust(MatchDesign design, double qMin = DefaultQMin)
    {
        ArgumentNullException.ThrowIfNull(design);

        var basis = MatchSynthesis.Synthesize(design);
        if (!basis.Ok) return null;

        bool anaIsTerm1 = basis.AnalysisIsTerm1;
        Termination ana = anaIsTerm1 ? design.Term1 : design.Term2;
        double om0 = design.Omega0, r = ana.R;
        double ceq = ana.CeqAt(om0);
        bool series = ana.Topology == TerminationTopology.Series;

        double lo, hi;
        if (series) { hi = ceq > 0 ? ceq : 1e-12; lo = hi / 10.0; }
        else { lo = ceq > 0 ? ceq : 1e-15; hi = lo * 10.0; }

        double? recorded = null;
        for (int step = 0; step < 15; step++)
        {
            double guess = 0.5 * (lo + hi);
            double q = series ? 1.0 / (om0 * r * guess) : om0 * r * guess;

            var probe = design.Clone();
            probe.QAdjust = q;
            var probeBasis = MatchSynthesis.Synthesize(probe);
            bool any = probeBasis.Ok && SolutionsFor(probe, probeBasis, q).Any();

            if (any)
            {
                recorded = guess;
                if (series) lo = guess; else hi = guess;
            }
            else
            {
                if (series) hi = guess; else lo = guess;
            }
        }

        if (recorded is not { } g) return null;
        double qFinal = series ? 1.0 / (om0 * r * g) : om0 * r * g;
        qFinal = Math.Max(qFinal, qMin);
        if (qFinal <= 0) qFinal = 0.01;
        return qFinal;
    }

    /// <summary>A stable identity for a solution: order, response, Q-adjust and the ordered pair names.</summary>
    public static string SolutionFingerprint(MatchDesign design, IReadOnlyList<TransformRecord> transforms)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(transforms);

        var sb = new StringBuilder();
        sb.Append(design.Order).Append('|').Append(design.Response).Append('|')
          .Append(design.QAdjust.ToString("G6", CultureInfo.InvariantCulture)).Append('|');
        foreach (var t in transforms)
            sb.Append(t.ElementA).Append('-').Append(t.ElementB).Append(':').Append(t.Form).Append(',');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
