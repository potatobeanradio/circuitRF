// §2.4's ranked per-element table — arithmetic over a SOLVED netlist, and nothing else
// (docs/design/railrf.md §2.4, §2.6; docs/sonnet-briefs/brief-railrf-5-dc-solve.md R-rail5-3 …
//  R-rail5-5).
//
// ── WHY A TOTAL IS NOT THE OUTPUT ──────────────────────────────────────────────────────────────
//
// §2.4: "'This 42 mm run of 0.3 mm inner-layer copper is 38 % of your drop' is a finding; 'the drop
// is 180 mV' is not." Rev 3 of the design note exists for the correction under it: sheet resistance
// is 0.49 mΩ/square at 1 oz and 0.99 mΩ/square at 0.5 oz, a 50 mm run of 0.3 mm 0.5 oz copper is
// 167 squares and therefore ~165 mΩ — HALF a 350 mΩ protection FET. So THE COPPER IS NEAR THE TOP
// OF THIS TABLE, not the bottom, and nothing here may special-case it as a small term or round it
// away.
//
// ── NO DOMAIN TYPES, AND NO MATRIX ─────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (overview §1a), so this file names no Technology, no LayerKey and
// no PdnNetlist: it takes node pairs, resistances and currents, and gives back rows. The GROUPING —
// which elements are one trace section on one layer, which vias are one group — is the caller's,
// because it is the caller that holds the origins. And per the series' own scope rule, nothing under
// src/Engine/Pdn builds a matrix or factorises anything.

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One element of the solved netlist, with the group it belongs to.
/// </summary>
/// <param name="GroupKey">What makes this element one row with others — a trace section, a via
/// group, a part. Rows come back in this key's own aggregate.</param>
/// <param name="Label">The sentence the row reads as. The first element of a group supplies it.</param>
/// <param name="NodeA">The node the current is measured as leaving.</param>
/// <param name="NodeB">The node it arrives at.</param>
/// <param name="ResistanceOhms">Its DC resistance.</param>
/// <param name="CurrentA">The current through it, positive from <paramref name="NodeA"/> to
/// <paramref name="NodeB"/>.</param>
public readonly record struct PdnBreakdownElement(
    string GroupKey, string Label, int NodeA, int NodeB, double ResistanceOhms, double CurrentA);

/// <summary>One row of §2.4's ranked breakdown.</summary>
/// <param name="Label">What it is — "42 mm of 0.3 mm inner copper, L3", "Q1 protection FET".</param>
/// <param name="ResistanceOhms">The group's own resistance, seen between its terminals.</param>
/// <param name="CurrentA">The current the group carries.</param>
/// <param name="DropV">The voltage across it.</param>
/// <param name="ShareOfTotal">Its fraction of every row's drop, in 0…1. The shares sum to one.</param>
public sealed record PdnBreakdownRow(
    string Label,
    double ResistanceOhms,
    double CurrentA,
    double DropV,
    double ShareOfTotal)
{
    /// <summary>How many netlist elements this row aggregates. One for a part; thousands for a
    /// meshed trace section — which is R-rail5-4's whole point.</summary>
    public int ElementCount { get; init; } = 1;

    /// <summary>The group key this row came from, so a caller can map a row back to the copper it
    /// names (the drop map, brief 8).</summary>
    public string GroupKey { get; init; } = "";
}

/// <summary>§2.4's ranked breakdown, as arithmetic.</summary>
public static class PdnBreakdown
{
    /// <summary>
    /// The ranked table.
    ///
    /// <para><b>The three numbers on a row are derived from DISSIPATION, and that is what makes them
    /// exact for every shape a group takes.</b> A group's dissipation is <c>Σ Iₑ²Rₑ</c> and its
    /// THROUGH CURRENT is half the sum, over every node, of the net current its own elements deliver
    /// there — which cancels at a node interior to the group and does not at a terminal. From those
    /// two: <c>drop = P / I</c> and <c>R = P / I²</c>.</para>
    ///
    /// <para>Take the three shapes R-rail5-4 aggregates and it is the same arithmetic each time. A
    /// series chain: every internal node cancels, <c>I</c> is the common current, <c>R</c> is
    /// <c>ΣRₑ</c> and the drop is <c>I·ΣRₑ</c>. Six via barrels between one pair of cells:
    /// <c>I</c> is their SUM, <c>R</c> is their parallel combination, and the drop is the voltage
    /// actually across them — never six times one barrel's. A meshed sheet with one way in and one
    /// way out: <c>R</c> is its spreading resistance. Summing resistances instead would be right for
    /// the first and wrong by a factor of six for the second.</para>
    ///
    /// <para><b>Ranked by drop</b>, because that is what §2.4 ranks on and it is not the same order
    /// as resistance: the aged cell's ESR and the 42 mm of inner copper trade places by the current
    /// they carry, not by the ohms they are.</para>
    /// </summary>
    public static IReadOnlyList<PdnBreakdownRow> Rank(IEnumerable<PdnBreakdownElement> elements)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);

        foreach (var e in elements)
        {
            if (!groups.TryGetValue(e.GroupKey, out var g))
            {
                groups[e.GroupKey] = g = new Group(e.Label);
                order.Add(e.GroupKey);
            }

            g.Power += e.CurrentA * e.CurrentA * e.ResistanceOhms;
            g.SumResistance += e.ResistanceOhms;
            g.Count++;

            // Positive current LEAVES NodeA and ARRIVES at NodeB, which is the engine's own branch
            // convention. Ground is a node like any other here: a group that returns current to it
            // has a terminal there, and dropping it would halve that group's through current.
            g.Net.TryGetValue(e.NodeA, out double a);
            g.Net[e.NodeA] = a - e.CurrentA;
            g.Net.TryGetValue(e.NodeB, out double b);
            g.Net[e.NodeB] = b + e.CurrentA;
        }

        var rows = new List<PdnBreakdownRow>(order.Count);
        double total = 0;

        foreach (string key in order)
        {
            var g = groups[key];

            double through = 0;
            foreach (double net in g.Net.Values) through += Math.Abs(net);
            through *= 0.5;

            double drop = through > 0 ? g.Power / through : 0.0;
            double ohms = through > 0 ? g.Power / (through * through) : g.SumResistance;

            total += drop;
            rows.Add(new PdnBreakdownRow(g.Label, ohms, through, drop, 0.0)
            {
                ElementCount = g.Count,
                GroupKey     = key,
            });
        }

        // Shares are computed against the sum of the rows themselves, so they add to one exactly
        // whatever the board is — a table whose percentages did not add up would be read as a
        // missing row, which is the one reading this table must never invite.
        for (int i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { ShareOfTotal = total > 0 ? rows[i].DropV / total : 0.0 };

        rows.Sort(static (p, q) =>
        {
            int byDrop = q.DropV.CompareTo(p.DropV);
            if (byDrop != 0) return byDrop;
            int byOhms = q.ResistanceOhms.CompareTo(p.ResistanceOhms);
            return byOhms != 0 ? byOhms : string.CompareOrdinal(p.GroupKey, q.GroupKey);
        });

        return rows;
    }

    private sealed class Group(string label)
    {
        public string Label { get; } = label;
        public double Power;
        public double SumResistance;
        public int Count;
        public readonly Dictionary<int, double> Net = [];
    }
}
