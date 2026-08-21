namespace CircuitRF.Core.Matching;

/// <summary>
/// What a value-driven transform search settled on.
/// </summary>
/// <param name="N">The turns ratio of every transform, in the design's own order.</param>
/// <param name="DrivenIndex">
/// Which transform the search moved directly; the rest followed the linkage. <b>−1 when the rack was
/// already as close as it can get</b>, in which case <paramref name="N"/> is what it already holds and
/// there is nothing to write.
/// </param>
/// <param name="Achieved">The value the named element actually reaches at <paramref name="N"/>.</param>
/// <param name="Exact">True when <see cref="Achieved"/> is the requested value to a relative 1e-6.</param>
public sealed record MatchElementSolution(
    IReadOnlyList<double> N, int DrivenIndex, double Achieved, bool Exact);

/// <summary>
/// <b>"Make this element that value, and do not move the response."</b> — the search behind the
/// network pane's inline value editor (owner, 2026-08-20).
/// </summary>
/// <remarks>
/// <b>A Norton transform is the only degree of freedom there is.</b> The element values of a
/// synthesised ladder are fixed by the specification; what the Designer can move without changing the
/// two-port's transfer function is where each transform sits (match.md §4.7). So a typed value is not
/// written to an element — it is a TARGET, and this is the search for the transform setting that comes
/// closest to it.
///
/// <para><b>Π N² is held on target throughout, which is what "do not move the response" means.</b>
/// A transform re-scales the far termination by N², so moving one N on its own leaves the ladder
/// matched to a resistance the user does not have. Every candidate here therefore goes through
/// <see cref="MatchLinkage.Redistribute"/> with the link ON, whatever the design's own link setting
/// is — the other unlocked transforms take up the slack and the product stays where it was.</para>
///
/// <para><b>A LOCKED transform is never written, and never driven either</b> (owner, 2026-08-20: "do
/// not allow for any locked transforms… sliders that are locked cannot ever change unless they are
/// unlocked first"). <see cref="MatchLinkage.Redistribute"/> already refuses to write one; this search
/// additionally refuses to pick one as the transform it drives, so a lock is a lock from both
/// directions.</para>
///
/// <para><b>Why a sampled sweep and not a solve.</b> The element's value as a function of one N is a
/// rational function of the whole ladder AFTER every other transform has been redistributed around
/// it — it is smooth, but it is not monotone in general and it has poles at the positivity
/// thresholds the range brackets. A coarse sweep finds which basin the target is in and a ternary
/// refinement lands on it; a Newton step from the current N would happily walk into a pole. Each
/// evaluation is <see cref="MatchRebuild.ApplySequence"/> over a handful of elements — arithmetic, no
/// synthesis and no engine — so a few hundred of them cost microseconds.</para>
/// </remarks>
public static class MatchElementSolve
{
    /// <summary>How many points the coarse sweep takes across one transform's range.</summary>
    public const int SweepPoints = 64;

    /// <summary>How many ternary-refinement steps run inside the winning bracket.</summary>
    public const int RefineSteps = 60;

    /// <summary>Relative distance at which a reached value counts as the value that was asked for.</summary>
    public const double ExactTolerance = 1e-6;

    /// <summary>
    /// How much better than the rack's CURRENT setting a candidate has to be before it is taken —
    /// relative, and generous enough that floating-point noise never moves a slider.
    /// </summary>
    public const double Improvement = 1e-9;

    /// <summary>
    /// The best reachable setting for <paramref name="elementName"/> at <paramref name="target"/>, or
    /// null when nothing can move it — no unlocked transform, or none whose range is usable.
    /// </summary>
    /// <param name="design">The working design. <b>Not modified.</b></param>
    /// <param name="basis">The synthesis every candidate is re-applied to.</param>
    /// <param name="ranges">
    /// Each transform's range as the last rebuild recomputed it, or null for one that dropped. Passed
    /// in rather than derived so the search bounds are the very ones the sliders are showing.
    /// </param>
    /// <param name="elementName">The ladder name of the element being aimed at.</param>
    /// <param name="target">The value asked for, in SI base units.</param>
    public static MatchElementSolution? Solve(
        MatchDesign design, MatchSynthesisResult basis,
        IReadOnlyList<TransformRange?> ranges, string elementName, double target)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentException.ThrowIfNullOrEmpty(elementName);
        if (!basis.Ok || !double.IsFinite(target) || target <= 0) return null;

        var records = design.Transforms;
        if (records.Count == 0) return null;

        double required = basis.RequiredTransformRatio;
        if (!double.IsFinite(required) || required <= 0) required = 1.0;

        var slots = new List<LinkSlot>(records.Count);
        for (int i = 0; i < records.Count; i++)
            slots.Add(new LinkSlot(records[i].N, records[i].Locked, RangeAt(ranges, i, records[i])));

        // The INCUMBENT is the rack as it stands, and a candidate has to beat it to be taken.
        //
        // Without this a target no transform can reach — L1 in a ladder whose transforms all act
        // further along it, say — still moved every slider, because every sample scored the same and
        // the sweep kept whichever it happened to see first. The user asked for a value they could
        // not have and got a rack rearranged for nothing, with the element unchanged. Seeding with
        // the current setting makes "it cannot be done" mean "nothing happens".
        var current = records.Select(r => r.N).ToList();
        MatchElementSolution? best = ValueAt(current) is { } here
            ? new MatchElementSolution(current, -1, here, RelativeError(here, target) <= ExactTolerance)
            : null;
        double bestError = best is null ? double.PositiveInfinity : RelativeError(best.Achieved, target);

        for (int i = 0; i < records.Count; i++)
        {
            // A locked transform is not driven. A dropped one has no range of its own, so the pinned
            // range RangeAt hands back makes it a no-op — refusing it here says so out loud.
            if (records[i].Locked) continue;
            var range = ranges.Count > i ? ranges[i] : null;
            if (range is null || !range.IsUsable) continue;

            Consider(Sweep(i, range));
        }

        return best;

        void Consider(MatchElementSolution? candidate)
        {
            if (candidate is null) return;
            double error = RelativeError(candidate.Achieved, target);
            // Strictly better by a real margin — a tie, or an improvement in the fifteenth digit, is
            // not worth moving a rack of sliders for.
            if (!(error < bestError - Improvement)) return;
            bestError = error;
            best = candidate;
        }

        MatchElementSolution? Sweep(int index, TransformRange range)
        {
            double lo = range.Min, hi = range.Max;
            double bestN = double.NaN, bestValue = double.NaN, bestErr = double.PositiveInfinity;
            int bestStep = -1;

            for (int k = 0; k < SweepPoints; k++)
            {
                double n = lo + (hi - lo) * k / (SweepPoints - 1.0);
                if (Evaluate(index, n) is not { } v) continue;
                double err = RelativeError(v, target);
                if (err >= bestErr) continue;
                bestErr = err; bestN = n; bestValue = v; bestStep = k;
            }

            if (bestStep < 0) return null;

            // Ternary refinement inside the winning bracket. |value − target| is smooth and has one
            // minimum inside a single sweep cell, which is the only span this ever searches.
            double step = (hi - lo) / (SweepPoints - 1.0);
            double a = Math.Max(lo, bestN - step);
            double b = Math.Min(hi, bestN + step);

            for (int it = 0; it < RefineSteps && b - a > 1e-15 * Math.Max(1.0, Math.Abs(b)); it++)
            {
                double m1 = a + (b - a) / 3.0;
                double m2 = b - (b - a) / 3.0;
                double e1 = Evaluate(index, m1) is { } v1 ? RelativeError(v1, target) : double.PositiveInfinity;
                double e2 = Evaluate(index, m2) is { } v2 ? RelativeError(v2, target) : double.PositiveInfinity;
                if (e1 <= e2) b = m2; else a = m1;
            }

            double refined = (a + b) / 2.0;
            if (Evaluate(index, refined) is { } rv && RelativeError(rv, target) < bestErr)
            {
                bestN = refined;
                bestValue = rv;
                bestErr = RelativeError(rv, target);
            }

            return new MatchElementSolution(
                Vector(index, bestN), index, bestValue, bestErr <= ExactTolerance);
        }

        IReadOnlyList<double> Vector(int index, double n) =>
            MatchLinkage.Redistribute(slots, index, n, required, link: true).N;

        double? Evaluate(int index, double n) => ValueAt(Vector(index, n));

        double? ValueAt(IReadOnlyList<double> n)
        {
            var candidate = new List<TransformRecord>(records.Count);
            for (int k = 0; k < records.Count; k++)
                candidate.Add(records[k].Locked ? records[k] : records[k] with { N = n[k] });

            var seq = MatchRebuild.ApplySequence(basis, candidate, design.AllowNegativeComponents);
            var net = MatchSynthesis.WithEndSplits(seq.Network, basis, design);
            var element = net.Elements.FirstOrDefault(
                e => string.Equals(e.Name, elementName, StringComparison.Ordinal));
            return element is null || !double.IsFinite(element.Value) || element.Value <= 0
                ? null
                : element.Value;
        }
    }

    /// <summary>
    /// A transform's search range: the recomputed one, or — for a dropped transform, which has no pair
    /// left to bound — its own value pinned, so the linkage arithmetic stays well-defined without
    /// inventing a bound. The same substitution <c>MatchDesignerViewModel.RangeFor</c> makes.
    /// </summary>
    private static TransformRange RangeAt(
        IReadOnlyList<TransformRange?> ranges, int index, TransformRecord record) =>
        index < ranges.Count && ranges[index] is { } r
            ? r
            : new TransformRange(record.N, record.N, true, record.N, record.N > 1.0);

    /// <summary>Relative distance from <paramref name="target"/>, which is the scale-free error a
    /// component value wants: 1 pF out on a 10 pF part is not 1 pF out on a 1 nH one.</summary>
    private static double RelativeError(double value, double target) =>
        !double.IsFinite(value) ? double.PositiveInfinity
        : Math.Abs(value - target) / Math.Max(Math.Abs(target), double.Epsilon);
}
