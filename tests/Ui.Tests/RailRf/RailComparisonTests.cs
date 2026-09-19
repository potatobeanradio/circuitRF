// ================================================================
//  RailComparisonTests.cs — brief-railrf-16-ab-comparison.md §5
//
//  The A/B comparison: the match, the delta, the paired removal rankings and the report.
//
//  §7's gate, and it is the design of this whole file:
//
//      The A/B report against TWO SYNTHETIC BOARDS DIFFERING IN EXACTLY ONE KNOWN WAY, where the
//      correct answer is CONSTRUCTED RATHER THAN SOLVED FOR.
//
//  So every pair below is built from one `Board()` and one edit to it, and the expected finding is
//  written down before circuitRF is asked — a closed-form resonance, a closed-form trace resistance,
//  or a row that is simply absent. The row that catches the most is the last one: two designs that
//  differ in NOTHING must produce an identically zero delta, because a match that silently paired
//  the wrong things produces a non-zero delta on identical inputs and every other test here would
//  still pass.
//
//  No window, no app host, no Avalonia in the model tests. The last two reach src/Ui to prove the
//  page goes through brief 9's one render function, which is the only reason this file lives in
//  Ui.Tests at all.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailComparisonTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // ── fixtures: one board, and one edit to it ──────────────────────────────
    //
    //  Q-9's own three parts, the same ones PdnImpedanceTests is built on, so the resonances here
    //  are numbers that file already gates against a closed form: a 470 µF bulk on 5 nH of mounting
    //  resonates near 104 kHz and a 100 nF 0402 on 1 nH near 16 MHz.

    private const string RailName = "VDD_CORE";
    private const double BulkMountingH = 5e-9;
    private const double CeramicMountingH = 1e-9;
    private const double CeramicFarads = 100e-9;

    private static PartLibrary Library()
    {
        var lib = new PartLibrary();
        lib.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-470U-BULK", CapacitanceFarads = 470e-6, EsrOhms = 50e-3,
        });
        lib.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-100N-0402", DielectricClass = "X7R",
            CapacitanceFarads = CeramicFarads, EsrOhms = 5e-3,
        });
        return lib;
    }

    private static RailPart Part(string refdes, string partNumber, double? mountingH) =>
        new() { Refdes = refdes, PartNumber = partNumber, MountingInductanceHenries = mountingH };

    private static RailLoad Load(string refdes, string pin = "VDD") =>
        new() { Anchor = new RailPortAnchor { Refdes = refdes, Pin = pin } };

    private static RailSource Source() =>
        new()
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.6,
            SeriesResistanceOhms = 1.0,
            SeriesInductanceHenries = 1e-6,
        };

    /// <summary>
    /// One design. <b>The two sides of every pair below are this same board</b>, with exactly one
    /// edit applied by the test — which is what makes the expected answer constructible.
    /// </summary>
    /// <param name="name">What the report calls it.</param>
    /// <param name="parts">Its decoupling, or the default bulk-plus-ceramic pair.</param>
    /// <param name="loads">Its observation ports.</param>
    /// <param name="extent">R-rail16-8's field.</param>
    private static RailDocument Board(
        string name,
        IEnumerable<RailPart>? parts = null,
        IEnumerable<RailLoad>? loads = null,
        RailReferenceExtent extent = RailReferenceExtent.AsImported,
        RailBand? band = null)
    {
        var rail = new RailSpec
        {
            Name            = RailName,
            NetName         = "VDD_CORE",
            ReferenceExtent = extent,
            // A flat target, so every peak has a margin and the removal ranking has a number to
            // move. 100 mΩ sits between the decoupled floor and the anti-resonance, which is what
            // makes a removal change the verdict at all.
            ImpedanceTarget = RailTarget.OfFlatImpedance(100.0),
            Band            = band ?? WholeBand,
        };

        rail.Sources.Add(Source());
        rail.Loads.AddRange(loads ?? [Load("U1")]);
        rail.Parts.AddRange(parts ??
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH)]);
        rail.Aggressors.Add(new RailAggressor("converter", 2.2e6, 8));

        var doc = new RailDocument { Name = name };
        doc.Rails.Add(rail);
        Assert.Null(doc.Refusal());
        return doc;
    }

    /// <summary>
    /// One design's frequency answer. <b>Every pair is swept on ONE explicit grid</b> — the reason
    /// is <see cref="PdnDelta"/>'s own: a resonance search adds points where THAT board's peaks
    /// are, so two boards swept from one band come back on two axes that differ exactly at the
    /// frequencies the comparison is about.
    /// </summary>
    private static PdnSweepResult Sweep(RailDocument doc, double[] grid)
    {
        var rail = doc.Rail(RailName)!;
        var result = PdnSweep.Run(new PdnSweepRequest
        {
            Rail          = rail,
            Parts         = new RailPartResolver(Library()).ResolveAll(rail.Parts, railVoltageV: 3.6),
            Sources       = [new RailSourceModel(0, "BT1", RailSourceBasis.Rl, 1.0, 1e-6, 3.6)],
            FrequenciesHz = grid,
            RankRemovals  = true,
        });
        Assert.Null(result.Refusal);
        return result;
    }

    /// <summary>10 kHz to 100 MHz — §11.3's own sketch, and the band every pair below uses unless
    /// it says otherwise.</summary>
    private static RailBand WholeBand => new(1e4, 1e8, 401, true);

    /// <summary>
    /// 10 kHz to 100 kHz — the bulk's own decade, and nothing above it.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a 0.0 dB row constructible at all, and the arithmetic behind it is
    /// worth stating.</b> A part is redundant when removing it leaves the worst margin where it was,
    /// and in a bank of parts in parallel that means contributing under ~0.6 % of the admittance at
    /// the worst-margin frequency — 0.05 dB is a ratio of 1.006. Removing one of N equal parts moves
    /// the answer by about 8.7/N dB, so no bank of a plausible size is redundant BY PARALLELISM: the
    /// real 0.0 dB row is a part that is OUT OF BAND where the judging happens, which is exactly
    /// §2.6's "shadowed by lower-inductance neighbours". Judged to 100 kHz, a 100 nF ceramic is
    /// 159 Ω against the bulk's 60 mΩ and contributes nothing measurable; the bulk itself still
    /// earns everything.
    /// </remarks>
    private static RailBand BulkBand => new(1e4, 1e5, 101, true);

    private static double[] Grid(RailDocument doc) => PdnSweep.Grid(doc.Rail(RailName)!.Band);

    private static RailComparisonSide Side(RailDocument doc, double[] grid) =>
        new() { Sweep = Sweep(doc, grid) };

    /// <summary>The whole feature, end to end, on one grid — the reference design's.</summary>
    private static RailComparisonReport Compare(RailDocument reference, RailDocument target)
    {
        double[] grid = Grid(reference);
        var match = RailComparison.Match(reference, target, RailName);
        return RailComparisonReport.Build(match, Side(reference, grid), Side(target, grid));
    }

    /// <summary>
    /// A frequency answer carrying nothing but a removal ranking.
    /// </summary>
    /// <remarks>
    /// <b>§7's own instruction: "the correct answer is CONSTRUCTED rather than solved for".</b>
    /// R-rail16-5 is a claim about how the report READS two rankings, not about what produces one —
    /// the physics of a 0.0 dB row is brief 12's gate and <c>PdnImpedanceTests</c> already holds it.
    /// Handing the report two rankings written down here is what makes "exactly one finding fires,
    /// and it is this one, by this refdes" an assertion rather than a hope about a solve.
    /// </remarks>
    private static PdnSweepResult Ranking(params (string Name, double GrowthDb, bool Redundant)[] rows) =>
        new(null, null, [], [],
            [.. rows.Select(r => new PdnRemovalRow(r.Name, 0.0, r.GrowthDb, r.Redundant))], [], []);

    /// <summary>The self-resonance of a part on its own mounting loop — the band the delta is
    /// expected to move in when that part changes. Closed form, and nothing about it comes out of
    /// circuitRF.</summary>
    private static double Resonance(double henries, double farads) =>
        1.0 / (2 * Math.PI * Math.Sqrt(henries * farads));

    // ── R-rail16-8: the one mismatch that is a refusal ───────────────────────

    /// <summary>
    /// <b>R-rail16-8.</b> Two sides computed against different reference extents refuse, <b>naming
    /// both</b>.
    /// </summary>
    /// <remarks>
    /// §2.2 gives <c>Infinite</c> partly for this — "the only way to compare two different outlines
    /// on equal terms" — and the corollary is that a comparison whose two sides used different
    /// extents is a comparison of two different return paths. There is no honest way to present
    /// that difference, because every number on both sides moves, which is why this is the one
    /// mismatch in the whole feature that stops the comparison rather than appearing on it.
    /// </remarks>
    [Fact]
    public void R_rail16_8_MismatchedReferenceExtents_Refuse_NamingBoth()
    {
        var match = RailComparison.Match(
            Board("reference", extent: RailReferenceExtent.AsImported),
            Board("target", extent: RailReferenceExtent.Infinite),
            RailName);

        Assert.NotNull(match.Refusal);
        _output.WriteLine(match.Refusal);

        Assert.Contains("as-imported", match.Refusal, StringComparison.Ordinal);
        Assert.Contains("infinite", match.Refusal, StringComparison.Ordinal);
        Assert.Contains("reference", match.Refusal, StringComparison.Ordinal);
        Assert.Contains("target", match.Refusal, StringComparison.Ordinal);

        // And the refusal propagates: a report built on it carries the same sentence and invents no
        // sections, rather than presenting an empty comparison as a passing one.
        var report = RailComparisonReport.Build(
            match, new RailComparisonSide(), new RailComparisonSide());
        Assert.Equal(match.Refusal, report.Refusal);
        Assert.Empty(report.Sections);
        Assert.False(report.Equivalent);
    }

    // ── R-rail16-2: matched by refdes, and never by anything geometric ───────

    /// <summary>
    /// <b>R-rail16-2.</b> A part present on one board only is reported <b>unmatched</b>, by refdes,
    /// with the reason on the row.
    /// </summary>
    [Fact]
    public void R_rail16_2_APartOnOneBoardOnly_IsReportedUnmatched()
    {
        var reference = Board("reference");
        var target = Board("target", parts:
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH),
             Part("C9", "PN-100N-0402", CeramicMountingH)]);

        var match = RailComparison.Match(reference, target, RailName);

        Assert.Null(match.Refusal);
        Assert.Equal(2, match.Parts.Count);
        Assert.False(match.Complete);

        var row = Assert.Single(match.Unmatched);
        _output.WriteLine(row.Describe());
        Assert.Equal("C9", row.Key);
        Assert.Equal(RailSide.Target, row.Side);
        Assert.Equal(RailMatchRule.Refdes, row.Rule);

        // Rule 3 declined it rather than reaching for it: C3 already claimed the one PN-100N-0402
        // on the reference, so there is no unambiguous part-number pairing left and railRF says so
        // instead of choosing.
        Assert.DoesNotContain(match.Parts, p => p.By == RailMatchRule.PartNumber);
    }

    /// <summary>
    /// <b>R-rail16-2's negative, and it is the half that matters.</b> A port 0.2 mm from a matched
    /// one on the other design stays <b>unmatched</b> — nothing pairs by proximity.
    /// </summary>
    /// <remarks>
    /// <b>The structural half is asserted too.</b> <see cref="RailComparison.Match"/> takes two
    /// DOCUMENTS and nothing else: no shapes, no technology, no placement. So there is no coordinate
    /// in scope to pair by and the prohibition cannot be violated without widening the signature —
    /// a stronger guarantee than the behavioural assertion above it, and the reason the parameter
    /// list is what it is. The source scan is what holds it shut.
    /// </remarks>
    [Fact]
    public void R_rail16_2_APortAFifthOfAMillimetreAway_IsStillUnmatched()
    {
        // 1000 DBU per micron is the layout editor's default, so 0.2 mm is 200 000 000 DBU.
        const long Dbu = 1000;
        long near = (long)(0.2 * 1000 * Dbu);

        var reference = Board("reference", loads:
            [new RailLoad { Anchor = new RailPortAnchor { Point = (0, 0) } }]);
        var target = Board("target", loads:
            [new RailLoad { Anchor = new RailPortAnchor { Point = (near, 0) } }]);

        var match = RailComparison.Match(reference, target, RailName);

        Assert.Null(match.Refusal);
        Assert.Empty(match.Loads);
        Assert.Equal(2, match.Unmatched.Count(u => u.Rule == RailMatchRule.Anchor));
        foreach (var row in match.Unmatched) _output.WriteLine(row.Describe());

        // The same two ports at EXACTLY the same point do pair — an identity, not a nearness, which
        // is what makes the assertion above a statement about proximity rather than about
        // coordinate anchors being unusable.
        var same = RailComparison.Match(
            reference,
            Board("target", loads: [new RailLoad { Anchor = new RailPortAnchor { Point = (0, 0) } }]),
            RailName);
        Assert.Single(same.Loads);

        // And the structural half: no geometry reaches the matcher at all.
        string source = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Design", "RailRf", "RailComparison.cs")));

        foreach (string forbidden in new[]
        {
            "LayoutView", "Technology", "LayoutShape", "PdnPlacement", "Math.Sqrt", "Math.Abs",
            "Distance", "Nearest", "Tolerance",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ── R-rail16-3: a coordinate-anchored port says so ───────────────────────

    /// <summary>
    /// <b>R-rail16-3.</b> A port anchored by coordinate on <b>either</b> side is reported as such —
    /// not refused, because sometimes it is all there is, but never silent.
    /// </summary>
    [Fact]
    public void R_rail16_3_ACoordinateAnchoredPort_IsSaidOnTheReport()
    {
        var reference = Board("reference", loads:
            [new RailLoad { Anchor = new RailPortAnchor { Point = (12_345, 6_789) } }]);
        var target = Board("target", loads:
            [new RailLoad { Anchor = new RailPortAnchor { Point = (12_345, 6_789) } }]);

        var match = RailComparison.Match(reference, target, RailName);
        var pair = Assert.Single(match.Loads);

        Assert.True(pair.ByCoordinate);
        Assert.Single(match.CoordinateAnchored);

        double[] grid = Grid(reference);
        var report = RailComparisonReport.Build(match, Side(reference, grid), Side(target, grid));
        string page = string.Join("\n", report.Sections.SelectMany(s => s.Lines));
        _output.WriteLine(pair.CoordinateCaveat());

        Assert.Contains("anchored by coordinate", page, StringComparison.Ordinal);

        // And the same document with a refdes-and-pin anchor says nothing of the kind, so the
        // assertion above is about the caveat rather than about some sentence that is always there.
        var clean = RailComparison.Match(Board("reference"), Board("target"), RailName);
        Assert.Empty(clean.CoordinateAnchored);
    }

    // ── R-rail16-5: the two findings out of one pairwise reading ─────────────

    /// <summary>
    /// <b>R-rail16-5's first finding.</b> A part that earns its place on the reference and earns
    /// nothing on the judged design has been <b>shadowed by the re-layout</b> — reported by refdes.
    /// </summary>
    /// <remarks>
    /// <b>The two rankings are written down rather than solved for</b> — §7's own instruction, and
    /// see <see cref="Ranking"/> for why it is the right oracle for this claim. C3 earns 8.1 dB on
    /// the reference and 0.0 dB on the judged design; C1 is unchanged on both. So exactly one
    /// finding can fire, it has to be the first one, and it has to be C3 — an assertion no solve
    /// could make this sharp.
    /// </remarks>
    [Fact]
    public void R_rail16_5_APartShadowedByTheRelayout_IsFoundByRefdes()
    {
        var reference = Board("reference");
        var target    = Board("target");

        var report = RailComparisonReport.Build(
            RailComparison.Match(reference, target, RailName),
            new RailComparisonSide
            {
                Sweep = Ranking(("C1 (PN-470U-BULK)", 14.2, false), ("C3 (PN-100N-0402)", 8.1, false)),
            },
            new RailComparisonSide
            {
                Sweep = Ranking(("C1 (PN-470U-BULK)", 14.2, false), ("C3 (PN-100N-0402)", 0.0, true)),
            });

        foreach (var row in report.Removal) _output.WriteLine(row.Describe());

        var c3 = Assert.Single(report.Removal, r => r.Finding == RailRemovalFinding.Shadowed);
        Assert.Equal("C3", c3.Name);
        Assert.False(c3.Reference!.Redundant);
        Assert.True(c3.Target!.Redundant);

        // Exactly one finding, and C1 — unchanged on both boards — produces none.
        Assert.Equal(RailRemovalFinding.None,
                     Assert.Single(report.Removal, r => r.Name == "C1").Finding);

        // The finding is on the page by refdes, not left for the reader to diff two tables.
        string page = string.Join("\n", report.Sections.SelectMany(s => s.Lines));
        Assert.Contains("C3", page, StringComparison.Ordinal);
        Assert.Contains("SHADOWED", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>R-rail16-5's second finding.</b> A part that earns nothing on <b>both</b> designs can come
    /// off both boards.
    /// </summary>
    /// <remarks>
    /// <b>This half is SOLVED, not constructed, and it is the physical case §2.6 describes.</b>
    /// Judged over the bulk's own decade (see <see cref="BulkBand"/>), a 100 nF ceramic is three
    /// orders of magnitude above the bulk it sits beside and moves the worst margin by nothing at
    /// all — so it reads 0.0 dB on both designs and can come off both boards. The bulk itself is
    /// the negative half: it earns everything, so the finding names one part and not the table.
    /// </remarks>
    [Fact]
    public void R_rail16_5_APartThatEarnsNothingOnEither_ComesOffBothBoards()
    {
        var report = Compare(Board("reference", band: BulkBand), Board("target", band: BulkBand));

        foreach (var row in report.Removal) _output.WriteLine(row.Describe());

        var c3 = Assert.Single(report.Removal, r => r.Finding == RailRemovalFinding.RemovableOnBoth);
        Assert.Equal("C3", c3.Name);
        Assert.True(c3.Reference!.Redundant && c3.Target!.Redundant);

        // The bulk holds this decade up on both designs, so it is not in the block a user is about
        // to delete.
        var c1 = Assert.Single(report.Removal, r => r.Name == "C1");
        Assert.False(c1.Reference!.Redundant);
        Assert.Equal(RailRemovalFinding.None, c1.Finding);

        // Nothing is shadowed: the two designs are the same design, so the finding that is ABOUT a
        // difference must not fire.
        Assert.DoesNotContain(report.Removal, r => r.Finding == RailRemovalFinding.Shadowed);
    }

    // ── §7's last row: two identical designs ─────────────────────────────────

    /// <summary>
    /// <b>§7's row that catches the whole class.</b> Two designs differing in nothing produce an
    /// <b>identically zero</b> delta, and the report says the boards are equivalent.
    /// </summary>
    /// <remarks>
    /// <b>Identically zero, asserted exactly.</b> The same arithmetic over the same inputs produces
    /// the same doubles, every ratio is exactly 1 and every point's delta is exactly 0 dB — so this
    /// is a <c>== 0</c> and not a tolerance. A match that silently paired the wrong things — U1's
    /// curve against U7's, C3's ranking row against C9's — produces a NON-zero delta on identical
    /// inputs, and every other test in this file would still pass.
    /// </remarks>
    [Fact]
    public void TwoIdenticalDesigns_HaveAnIdenticallyZeroDelta_AndTheReportSaysEquivalent()
    {
        var report = Compare(Board("reference"), Board("target"));

        var port = Assert.Single(report.Ports);
        _output.WriteLine($"max |Δ| = {port.Delta.MaxAbsDeltaDb:R} dB over {port.Delta.Points.Count} points");

        Assert.Null(port.Delta.Refusal);
        Assert.All(port.Delta.Points, p => Assert.Equal(0.0, p.DeltaDb));
        Assert.Equal(0.0, port.Delta.MaxAbsDeltaDb);
        Assert.Empty(port.Delta.Excursions);
        Assert.True(port.Delta.Equivalent);

        Assert.True(report.Equivalent);
        Assert.Empty(report.Match.Unmatched);

        string findings = string.Join(" ", report.Findings);
        _output.WriteLine(findings);
        Assert.Contains("equivalent", findings, StringComparison.OrdinalIgnoreCase);
    }

    // ── §7's first row: one trace's width ────────────────────────────────────

    /// <summary>
    /// <b>§7's first row.</b> Two designs differing only in one trace's width move <b>one</b> DC
    /// breakdown row, by the closed-form amount — and the impedance does not move at all.
    /// </summary>
    /// <remarks>
    /// <b>The oracle is R = ρL/(WT), not circuitRF.</b> A 50 mm run of 0.5 oz copper (17.4 µm) at
    /// 0.3 mm wide is 165 mΩ; the same run at 0.15 mm is exactly twice that, because halving the
    /// width doubles the number of squares and nothing else in the expression moves. At the 0.9 A
    /// this rail draws the extra 165 mΩ is 148.5 mV, which is §2.5's own DC surprise — "the same
    /// schematic, 40 mm of extra 0.3 mm trace, and 60 mV that were not in the budget" — in the form
    /// this test can construct.
    ///
    /// <para>The second half is the one that would catch a comparison doing something: a DC-only
    /// difference must leave every port's Δ|Z| identically zero. A report that moved both halves
    /// from one edit is a report whose two halves are not independent.</para>
    /// </remarks>
    [Fact]
    public void OneTracesWidth_MovesOneDcRow_ByTheClosedFormAmount_AndTheImpedanceDoesNotMove()
    {
        const double Rho      = 1.72e-8;  // annealed copper, Ω·m
        const double Length   = 50e-3;
        const double Thick    = 17.4e-6;  // 0.5 oz
        const double Current  = 0.9;

        double wide   = Rho * Length / (0.30e-3 * Thick);
        double narrow = Rho * Length / (0.15e-3 * Thick);
        double expectedDeltaV = Current * (narrow - wide);

        _output.WriteLine($"0.30 mm → {wide * 1e3:0.###} mΩ · 0.15 mm → {narrow * 1e3:0.###} mΩ " +
                          $"· Δ = {expectedDeltaV * 1e3:0.###} mV at {Current} A");

        // Exactly twice, by construction — the arithmetic above, checked against itself before it is
        // used as an oracle.
        Assert.Equal(2.0, narrow / wide, 12);

        var reference = Board("reference");
        var target    = Board("target");
        double[] grid = Grid(reference);

        var match = RailComparison.Match(reference, target, RailName);
        var report = RailComparisonReport.Build(
            match,
            new RailComparisonSide { Sweep = Sweep(reference, grid), Dc = Dc(wide, Current) },
            new RailComparisonSide { Sweep = Sweep(target, grid),    Dc = Dc(narrow, Current) });

        foreach (var row in report.Breakdown) _output.WriteLine(row.Describe());

        var moved = report.Breakdown.Where(r => Math.Abs(r.DeltaV) > 1e-9).ToList();
        var trace = Assert.Single(moved);
        Assert.Equal("trace", trace.Reference!.GroupKey);
        Assert.Equal(expectedDeltaV, trace.DeltaV, 1e-9);

        // The other row — the protection FET — did not move, so the comparison changed one row and
        // not the table.
        Assert.Contains(report.Breakdown, r => r.Label.Contains("FET", StringComparison.Ordinal) &&
                                               Math.Abs(r.DeltaV) < 1e-12);

        // And the impedance half is untouched.
        Assert.All(report.Ports, p => Assert.True(p.Delta.Equivalent));

        static RailComparisonDc Dc(double traceOhms, double currentA) =>
            new(PdnBreakdown.Rank(
                [
                    new PdnBreakdownElement("trace", "50 mm of inner copper, L3", 1, 2, traceOhms, currentA),
                    new PdnBreakdownElement("Q1", "Q1 protection FET", 2, 3, 0.350, currentA),
                ]),
                []);
    }

    // ── §7's second row: one part's return via ───────────────────────────────

    /// <summary>
    /// <b>§7's second row.</b> Two designs differing only in one part's return-via distance move
    /// that part's mounting inductance, and the Δ trace moves <b>at that part's own band</b>.
    /// </summary>
    /// <remarks>
    /// <b>The band is a closed form.</b> C3 is a 100 nF part; on 1 nH of mounting it resonates at
    /// 15.9 MHz and on 2 nH at 11.3 MHz, so the delta between the two curves has to live between
    /// those two frequencies and nowhere near the bulk's 104 kHz. Asserting WHERE the delta is, and
    /// not merely that there is one, is what makes this a test of the pairing rather than of
    /// subtraction: a comparison that paired C1 with C3 would still produce a large delta, just not
    /// this one.
    /// </remarks>
    [Fact]
    public void OnePartsReturnVia_MovesItsMountingLoop_AndTheDeltaMovesAtThatPartsOwnBand()
    {
        var reference = Board("reference");
        var target = Board("target", parts:
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH * 2)]);

        var report = Compare(reference, target);

        // The per-part table first — §2.5's own sentence.
        var c3 = Assert.Single(report.Mounting, m => m.Name == "C3");
        _output.WriteLine(c3.Describe());
        Assert.Equal(CeramicMountingH, c3.ReferenceHenries!.Value, 1e-18);
        Assert.Equal(CeramicMountingH * 2, c3.TargetHenries!.Value, 1e-18);
        Assert.True(c3.Worse);
        Assert.Equal(1.0, c3.GrowthFraction!.Value, 1e-9);

        // C1 did not move, so the table names one part and not two.
        var c1 = Assert.Single(report.Mounting, m => m.Name == "C1");
        Assert.Equal(0.0, c1.DeltaHenries!.Value, 1e-18);
        Assert.False(c1.Worse);

        // And the delta lives in C3's own band.
        double loose = Resonance(CeramicMountingH * 2, CeramicFarads);
        double tight = Resonance(CeramicMountingH, CeramicFarads);
        double bulk  = Resonance(BulkMountingH, 470e-6);

        var port = Assert.Single(report.Ports);
        var worst = port.Delta.Excursions[0];
        _output.WriteLine($"{worst.Describe()}  ·  C3 resonates {tight / 1e6:0.#} MHz → " +
                          $"{loose / 1e6:0.#} MHz, the bulk at {bulk / 1e3:0.#} kHz");

        Assert.InRange(worst.FrequencyHz, loose / 2, tight * 2);
        Assert.True(worst.FrequencyHz > bulk * 10,
                    "the delta must not be at the bulk's own resonance — that would mean the two " +
                    "parts had been paired the wrong way round");
        Assert.False(report.Equivalent);
    }

    // ── §7's third row: one part deleted ─────────────────────────────────────

    /// <summary>
    /// <b>§7's third row.</b> A part on the reference and not on the judged design is absent from
    /// the judged design's removal ranking — and the delta shows its band.
    /// </summary>
    [Fact]
    public void OnePartDeleted_IsAbsentFromTheJudgedRanking_AndTheDeltaShowsItsBand()
    {
        var reference = Board("reference", parts:
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH)]);
        var target = Board("target", parts:
            [Part("C1", "PN-470U-BULK", BulkMountingH)]);

        var report = Compare(reference, target);

        foreach (var row in report.Removal) _output.WriteLine(row.Describe());

        // The row keeps the ranking's own spelling of a part it could not pair — refdes plus part
        // number — because an unmatched row has no matched refdes to be named by.
        var c3 = Assert.Single(report.Removal, r => r.Name.StartsWith("C3", StringComparison.Ordinal));
        Assert.NotNull(c3.Reference);
        Assert.Null(c3.Target);

        // It is UNMATCHED and said so, rather than quietly dropped — §2.5's own rule.
        var missing = Assert.Single(report.Match.Unmatched, u => u.Key == "C3");
        Assert.Equal(RailSide.Reference, missing.Side);
        _output.WriteLine(missing.Describe());

        // And the curve moved where C3 was working.
        double c3Resonance = Resonance(CeramicMountingH, CeramicFarads);
        var port = Assert.Single(report.Ports);
        var worst = port.Delta.Excursions[0];
        _output.WriteLine($"{worst.Describe()}  ·  C3 resonated at {c3Resonance / 1e6:0.#} MHz");

        Assert.True(worst.DeltaDb > 0, "deleting a decoupling part cannot lower the impedance");
        Assert.InRange(worst.FrequencyHz, c3Resonance / 4, c3Resonance * 4);
    }

    // ── PdnDelta's own decision: two grids are refused, not interpolated ─────

    /// <summary>
    /// Two sweeps on different grids are <b>refused</b>, naming the mismatch — never interpolated
    /// onto one axis.
    /// </summary>
    /// <remarks>
    /// This is not pedantry about array lengths. R-rail14-4's resonance search adds points where
    /// each board's own peaks are, so two boards swept from one band come back on two axes as a
    /// matter of course — and the two axes differ precisely at the frequencies the comparison is
    /// about. An interpolated target value would be invented exactly where the target curve is
    /// changing fastest and then reported as a several-decibel excursion with a frequency beside it.
    /// </remarks>
    [Fact]
    public void TwoDifferentGrids_AreRefused_RatherThanInterpolatedOntoOne()
    {
        double[] a = [1e4, 1e5, 1e6, 1e7];
        double[] b = [1e4, 1e5, 2e6, 1e7];
        double[] z = [1.0, 1.0, 1.0, 1.0];

        var refused = PdnDelta.Compute(a, z, b, z);
        _output.WriteLine(refused.Refusal);

        Assert.NotNull(refused.Refusal);
        Assert.Contains("1 MHz", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("2 MHz", refused.Refusal, StringComparison.Ordinal);
        Assert.Empty(refused.Points);
        Assert.False(refused.Equivalent);

        // A shorter axis is the same refusal, and it names both counts.
        var shorter = PdnDelta.Compute(a, z, [1e4, 1e5], [1.0, 1.0]);
        _output.WriteLine(shorter.Refusal);
        Assert.Contains("4", shorter.Refusal!, StringComparison.Ordinal);
        Assert.Contains("2", shorter.Refusal!, StringComparison.Ordinal);

        // On one grid it computes, and the sign is the one the header states: positive means the
        // JUDGED design is the higher impedance. 1 Ω against 2 Ω is +6.02 dB and not −6.02.
        var signed = PdnDelta.Compute(a, [1, 1, 1, 1], a, [2, 2, 2, 2]);
        Assert.Null(signed.Refusal);
        Assert.All(signed.Points, p => Assert.Equal(20 * Math.Log10(2.0), p.DeltaDb, 1e-12));
    }

    // ── R-rail16-6: the report, drawn by brief 9's one render function ───────

    /// <summary>
    /// <b>R-rail16-6.</b> The report is a REPORT — three sentences, with the coincidence check's own
    /// answer inside the third — and it is drawn by <c>RailReportPage</c>, the one function
    /// <c>Report ▸</c> and the headless report already call.
    /// </summary>
    /// <remarks>
    /// The SVG is asserted against Skia's own output, the way <c>RailCopyTests</c> and the
    /// <c>TryRenderToSvg</c> font tests do: the SVG device writes each text run's string into a
    /// <c>&lt;text&gt;</c> element, so a finding that is on the page is in those bytes and a finding
    /// that was composed into some other page is not.
    /// </remarks>
    [Fact]
    public void R_rail16_6_TheReportIsThreeSentences_AndItGoesThroughBrief9sRenderFunction()
    {
        var reference = Board("reference");
        var target = Board("target", parts:
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH * 4)]);

        var report = Compare(reference, target);

        foreach (string line in report.Findings) _output.WriteLine(line);

        // What changed, what it cost, what it broke — in that order, and the first block on the page.
        Assert.Equal(3, report.Findings.Count);
        Assert.Contains("mounting", report.Findings[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("What this comparison found", report.Sections[0].Heading);
        Assert.Equal(report.Findings, report.Sections[0].Lines);

        // R-rail16-7: the DC half is BELOW the impedance answer. Neither is present here (no DC was
        // supplied), so what is asserted is the order of the blocks that are.
        int impedance = Index(report, "Impedance");
        int delta     = Index(report, "Δ|Z|");
        int removal   = Index(report, "earn their place");
        Assert.True(impedance < delta && delta < removal);

        // The page. Every pixel of it is RailReportPage's.
        var page = RailComparisonExport.PageOf(report, board: null, tech: null, map: null);
        string svg = RailComparisonExport.BuildSvg(page);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("What this comparison found", svg, StringComparison.Ordinal);
        Assert.Contains("C3", svg, StringComparison.Ordinal);

        Assert.NotEmpty(RailComparisonExport.BuildPdf(page));

        static int Index(RailComparisonReport r, string fragment) =>
            r.Sections.ToList().FindIndex(s => s.Heading.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>R-rail16-6's gate.</b> There is no second render path: the comparison's page is composed
    /// by mapping sections onto <c>RailReportPage</c>, and nothing under <c>src/Ui/RailRf</c> or
    /// <c>src/Design/RailRf</c> draws one of its own.
    /// </summary>
    /// <remarks>
    /// §11.7: "there is one route from an overlay to a page and not two." Two compositions agree
    /// today and drift the first time either is touched, and the drift is invisible because both
    /// produce a plausible page — which is why this is a source scan rather than a picture
    /// comparison.
    /// </remarks>
    [Fact]
    public void R_rail16_6_ThereIsNoSecondRenderPath()
    {
        string export = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "RailRf", "RailComparisonExport.cs")));

        Assert.Contains("RailReportPage.Draw", export, StringComparison.Ordinal);
        foreach (string forbidden in new[] { "SKPaint", "SKFont", "DrawText", "DrawRect", "SKTypeface" })
            Assert.DoesNotContain(forbidden, export, StringComparison.Ordinal);

        // And the src/Design half draws nothing at all — it cannot even name the page type, which is
        // the boundary this feature is built on.
        foreach (string file in new[] { "RailComparison.cs", "RailComparisonReport.cs" })
        {
            string source = StripComments(File.ReadAllText(
                Path.Combine(RepoRoot(), "src", "Design", "RailRf", file)));

            foreach (string forbidden in new[]
                     { "SkiaSharp", "SKCanvas", "RailReportPage", "RailReportSection", "Avalonia" })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Comments stripped, because these files' own prose names the types they are being
    /// asserted not to call.</summary>
    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline),
                      @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
