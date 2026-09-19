// Which capacitors are earning their place — computed by actually REMOVING each one and
// re-solving (docs/design/railrf.md §2.4 "The capacitor ranking", §2.6 step 7, Q-4;
//  docs/sonnet-briefs/brief-railrf-12-impedance.md R-rail12-6).
//
// ── THE RE-SOLVE IS THE DESIGN, NOT AN IMPLEMENTATION DETAIL ───────────────────────────────────
//
// §2.4 states the reason out loud: "The ranking is computed by ACTUALLY REMOVING EACH PART AND
// RE-SOLVING, not by a sensitivity approximation — the solve is cheap and THE APPROXIMATION IS NOT
// TRUSTWORTHY NEAR AN ANTI-RESONANCE."
//
// That is not caution, it is arithmetic. At an anti-resonance the port admittance is the near
// cancellation of a large inductive susceptance against a large capacitive one, so d|Z|/dC is
// enormous and is the ratio of two quantities that are each orders of magnitude bigger than their
// difference. A first-order step from there predicts a change that the actual removal does not
// produce — and it predicts it CONFIDENTLY, with no sign that the linearisation has left its
// neighbourhood. Worse in the direction that matters: it over-states the importance of a part near
// the peak and under-states a part that is holding a decade somewhere else, which is exactly the
// judgement §2.6 step 7 asks a user to make.
//
// So this file takes a DELEGATE that re-solves. There is no sensitivity path here, not even as a
// fast option, because an option is a thing a future caller will reach for.
//
// ── NO DOMAIN TYPES ────────────────────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a): a part here is a name and an index, and what a
// re-solve IS belongs to the caller that holds the netlist.

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One part's row of §2.4's capacitor ranking.
/// </summary>
/// <param name="Name">The part, as the caller names it.</param>
/// <param name="WorstMarginDb">What the worst mask margin becomes with this part off the board,
/// or NaN where nothing could be judged without it.</param>
/// <param name="GrowthDb">How much the worst violation GROWS if this part is removed, in dB —
/// §2.4's own column. Positive means the margin fell by that much.</param>
/// <param name="Redundant">True where <paramref name="GrowthDb"/> is under the display resolution:
/// §2.6's "shadowed by lower-inductance neighbours", and a candidate for deletion.</param>
public sealed record PdnRemovalRow(
    string Name,
    double WorstMarginDb,
    double GrowthDb,
    bool Redundant)
{
    /// <summary>The sentence the ranking's own row reads.</summary>
    public string Describe() =>
        Redundant
            ? $"{Name}: 0.0 dB — removing it leaves the worst margin where it was. A candidate for " +
              "deletion."
            : $"{Name}: {GrowthDb:0.0} dB — removing it takes the worst margin to " +
              $"{WorstMarginDb:0.0} dB.";
}

/// <summary>§2.4's capacitor ranking.</summary>
public static class PdnRemovalRanking
{
    /// <summary>
    /// How small a change in the worst margin reads as no change at all, in DECIBELS.
    ///
    /// <para><b>0.05 dB, because the column is printed to one decimal.</b> §2.6's own reading of
    /// this table is "five parts show 0.0 dB", so the threshold is the one that makes the printed
    /// figure and the verdict agree: anything that rounds to 0.0 dB IS 0.0 dB on this table, and a
    /// part flagged redundant while its row read 0.1 would be a table arguing with itself.</para>
    /// </summary>
    public const double DisplayResolutionDb = 0.05;

    /// <summary>
    /// The ranking. One call to <paramref name="worstMarginWithout"/> per part — <b>one re-solve
    /// per part</b>, and never a sensitivity.
    /// </summary>
    /// <remarks>
    /// <b>Sorted by how much the margin moves, most first</b>, so the parts that are holding the
    /// design up are at the top and the 0.0 dB rows collect at the bottom as a block — which is how
    /// §2.6 step 7 reads it: <i>"five parts show 0.0 dB — shadowed by lower-inductance neighbours.
    /// Delete them: still passes at 1.8 dB."</i>
    /// </remarks>
    /// <param name="baselineWorstMarginDb">The worst margin with every part fitted.</param>
    /// <param name="names">The parts, in the caller's own order.</param>
    /// <param name="worstMarginWithout">Re-solves with the part at that index removed and returns
    /// the new worst margin. <b>NaN is a legitimate answer</b> — a board with nothing left to judge
    /// — and produces a row whose growth is NaN rather than a zero that would read as redundant.</param>
    public static IReadOnlyList<PdnRemovalRow> Rank(
        double baselineWorstMarginDb,
        IReadOnlyList<string> names,
        Func<int, double> worstMarginWithout)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(worstMarginWithout);

        var rows = new List<PdnRemovalRow>(names.Count);

        for (int i = 0; i < names.Count; i++)
        {
            double without = worstMarginWithout(i);
            double growth = baselineWorstMarginDb - without;

            // A removal that IMPROVES the margin is clamped to no growth rather than reported as a
            // negative one. It happens: taking a part off can move an anti-resonance away from the
            // frequency where the mask is tightest. But the column §2.4 defines is "how much the
            // worst violation GROWS", and a negative entry in it reads as advice to delete the part
            // — which this table is not entitled to give, because it has judged one rail against
            // one mask and knows nothing about why the part is there.
            rows.Add(new PdnRemovalRow(
                names[i],
                without,
                double.IsNaN(growth) ? double.NaN : Math.Max(0.0, growth),
                !double.IsNaN(growth) && growth < DisplayResolutionDb));
        }

        rows.Sort(static (p, q) =>
        {
            int byGrowth = Rank(q.GrowthDb).CompareTo(Rank(p.GrowthDb));
            return byGrowth != 0 ? byGrowth : string.CompareOrdinal(p.Name, q.Name);
        });

        return rows;

        // NaN sorts as the largest, because a part whose removal leaves nothing judgeable is the
        // opposite of redundant and must not sit in the block a user is about to delete.
        static double Rank(double growth) => double.IsNaN(growth) ? double.PositiveInfinity : growth;
    }

    /// <summary>
    /// The parts §2.6 step 7 says are candidates for deletion — the 0.0 dB block, in ranking order.
    /// </summary>
    public static IReadOnlyList<PdnRemovalRow> Redundant(IEnumerable<PdnRemovalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.Where(r => r.Redundant)];
    }
}
