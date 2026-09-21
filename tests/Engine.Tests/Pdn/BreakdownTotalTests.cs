// ================================================================
//  BreakdownTotalTests.cs — brief-railrf-21-two-numbers-one-name.md §4, gates 5 and 6
//
//  ── TWO TOTALS, BOTH CALLED THE DROP ────────────────────────────────────────────────────────
//
//  The Drop card reports a port's own drop; the Breakdown card's rows are shares of a DIFFERENT
//  total — the sum of every group's drop on the rail. On the shipped Power Rail example they were
//  50.131 mV and 48.368 mV, both labelled in millivolts, with nothing on screen saying they answer
//  different questions. A designer read the pair as a bug (2026-09-20). The example has been
//  re-spaced since and both figures have moved; the two totals still differ, and that is the point.
//
//  ── AND THE ARITHMETIC IS NOT THE BUG ───────────────────────────────────────────────────────
//
//  PdnBreakdown.Rank is right and its invariant is deliberate: shares are taken against the sum of
//  the rows so that they add to one exactly whatever the board is, because a table whose
//  percentages did not add up would be read as a missing row. So gate 6 pins that invariant while
//  gate 5 pins the new sentence — a label and an explanation, and nothing else.
//
//  ── THE TWO SYNTHETIC BOARDS ARE THE TEST ───────────────────────────────────────────────────
//
//  A series chain, where the rows telescope to the port's own drop and there is nothing to
//  reconcile; and a chain that DIVIDES between two legs and rejoins, where the port drops one leg
//  and the table lists both. The second is the shipped example's shape, reduced: on the board as it
//  stood then the 1.763 mV gap was exactly its second parallel leg (src/Design/RESOLVED.md carries
//  that row by row). The synthetic boards below are the test, so no number here follows the
//  example.
//
//  One test per CLAIM, not one per rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Engine.Pdn;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Pdn;

public sealed class BreakdownTotalTests(ITestOutputHelper output)
{
    // ══ gate 5 — R-rail21-2a/2b ══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The table states its own total by name; a board whose total differs from the port's drop
    /// prints the reconciliation, and one that agrees prints nothing.</b>
    /// </summary>
    [Fact]
    public void ABreakdownWhoseTotalDiffersExplainsItselfAndOneThatAgreesSaysNothing()
    {
        // ── the ordinary board: source, copper, load, all in series ───────────────────────────
        var series = PdnBreakdown.Rank(SeriesChain(out double seriesPortDrop));

        Assert.Equal(seriesPortDrop, PdnBreakdown.TotalDropV(series), 12);
        Assert.Contains("sum to", PdnBreakdown.TotalLine(series), StringComparison.Ordinal);
        Assert.Equal("", PdnBreakdown.Reconcile(series, "U1.VDD", seriesPortDrop));

        // ── the board that divides: two legs between one pair of nodes, and they rejoin ───────
        var split = PdnBreakdown.Rank(SplitChain(out double splitPortDrop, out double legDropV));
        double total = PdnBreakdown.TotalDropV(split);

        output.WriteLine($"rows sum to {total * 1e3:0.####} mV, the port is {splitPortDrop * 1e3:0.####} mV " +
                         $"below the source, one leg drops {legDropV * 1e3:0.####} mV");

        // The gap IS the second leg — the finding this sentence was written from, not a guess.
        Assert.Equal(legDropV, total - splitPortDrop, 12);

        string why = PdnBreakdown.Reconcile(split, "U1.VDD", splitPortDrop);
        Assert.NotEqual("", why);
        Assert.Contains("U1.VDD", why, StringComparison.Ordinal);
        Assert.Contains("parallel", why, StringComparison.Ordinal);
        Assert.Contains("Both numbers are right", why, StringComparison.Ordinal);
    }

    // ══ gate 6 — the invariant PdnBreakdown.Rank exists to keep ═══════════════════════════════

    /// <summary>
    /// <b>The shares still sum to one, on both boards.</b> Nothing in brief 21 touches
    /// <see cref="PdnBreakdown.Rank"/>, and this is what says so.
    /// </summary>
    [Fact]
    public void TheSharesStillSumToOne()
    {
        foreach (var rows in new[]
                 {
                     PdnBreakdown.Rank(SeriesChain(out _)),
                     PdnBreakdown.Rank(SplitChain(out _, out _)),
                 })
        {
            Assert.NotEmpty(rows);
            Assert.Equal(1.0, rows.Sum(r => r.ShareOfTotal), 12);
        }
    }

    // ══ the two boards ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Source (60 mΩ) → copper (65 mΩ) → the port, 350 mA all the way. Nothing branches, so the
    /// rows telescope and the sum IS the drop at the port.
    /// </summary>
    private static List<PdnBreakdownElement> SeriesChain(out double portDropV)
    {
        const double I = 0.350;
        portDropV = I * (0.060 + 0.065);

        return
        [
            new PdnBreakdownElement("src",    "the source's own series resistance", 1, 2, 0.060, I),
            new PdnBreakdownElement("copper", "26 mm of BOT copper",                2, 3, 0.065, I),
        ];
    }

    /// <summary>
    /// The same chain, with the run between nodes 3 and 4 divided into two legs that carry 290.8 mA
    /// and 59.2 mA and rejoin — the shipped example's own shape, reduced to the part that matters.
    /// Each leg drops the same voltage, because they are across one pair of nodes; the port drops it
    /// ONCE and the table lists it twice.
    /// </summary>
    private static List<PdnBreakdownElement> SplitChain(out double portDropV, out double legDropV)
    {
        const double I = 0.350, Ia = 0.2908, Ib = I - Ia;

        // Two legs across one node pair means one voltage, so their resistances are in that ratio.
        const double Ra = 0.0060;
        double rb = Ra * Ia / Ib;

        legDropV  = Ia * Ra;
        portDropV = I * (0.060 + 0.065) + legDropV;

        return
        [
            new PdnBreakdownElement("src",    "the source's own series resistance", 1, 2, 0.060, I),
            new PdnBreakdownElement("copper", "26 mm of BOT copper",                2, 3, 0.065, I),
            new PdnBreakdownElement("legA",   "3.9 mm of TOP copper",               3, 4, Ra,    Ia),
            new PdnBreakdownElement("legB",   "9.3 mm of BOT copper",               3, 4, rb,    Ib),
        ];
    }
}
