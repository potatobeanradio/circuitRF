// ================================================================
//  PdnDcSolveTests.cs — brief-railrf-5-dc-solve.md §7
//
//  The answer P0 exists for: the drop, the ranked breakdown, and the rail chain.
//
//  ── TWO OF THESE GATES ARE ARITHMETIC RATHER THAN OPINION ────────────────────────────────────
//
//  SUPERPOSITION needs no external data and no reference board: a resistive mesh is LINEAR, so the
//  response to a set of sources is exactly the sum of the responses to each of them with the others
//  ZEROED. Not removed — zeroed. A source that is removed leaves an OPEN where a zeroed one leaves
//  its own series resistance in circuit, and the two are different networks; the brief's own wording
//  ("solve with source A alone") is the loose form of it and this file uses the exact one, because a
//  gate that is only approximately superposition catches only approximately the defect it exists for.
//  That defect is §9's: "a second source stamped at the wrong node produces a plausible number, not
//  an error."
//
//  THE RAIL CHAIN is gated against a hand-solved two-rail ladder whose loop resistance is 2·R_s by
//  construction — the rail strip and the reference strip are the SAME geometry, which is §4.1's own
//  factor of two and the only shape of board whose return path has a closed form. The arithmetic is
//  in the comment beside the literal.
//
//  ── AND THE SECOND ASSERTION IS THE ONE THAT CATCHES THE REAL DEFECT ─────────────────────────
//
//  A chain that reads the NOMINAL passes "the input voltage is 3.63 V" the moment 3.63 happens to be
//  what the document says. What it cannot pass is: change the input rail's COPPER, and the OUTPUT
//  rail's answer moves. That is the assertion below, and it is why the ladder's second rail takes its
//  level from upstream rather than stating one.
//
//  ── NO TIMING, AND NO SECOND SOLVER ─────────────────────────────────────────────────────────
//
//  Unlike PdnMeshExtractorTests and PdnFastExtractorTests, this file does NOT carry a little
//  conjugate-gradient solver of its own: those two gate extractors that never solve, and this one
//  gates the solve itself. What stands in for an independent implementation here is arithmetic —
//  linearity, KCL, and a closed-form ladder.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnDcSolveTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private const double CopperSigma = 5.8e7;            // S/m at 20 °C
    private const double CopperRho = 1.0 / CopperSigma;  // 1.724138e-8 Ω·m

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static Technology Board(double topUm, double botUm)
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(topUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(botUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>Closed form for one straight section: <c>R = ρ·L/(W·T)</c>.</summary>
    private static double Section(double lengthM, double widthM, double thicknessM) =>
        CopperRho * lengthM / (widthM * thicknessM);

    // ── R-rail5-6: more than one source, gated by SUPERPOSITION ────────────────────────────────

    /// <summary>
    /// §7's cheapest real gate. Two sources on one rail, each with its own series resistance and its
    /// own pad: the two-source solve is EXACTLY the sum of the two one-source solves, node for node,
    /// to machine precision — not to a tolerance, because this is linearity and not physics.
    ///
    /// <para>The ports here state no current on purpose. A load's own injection is a third source and
    /// would appear in BOTH one-source solves, so the sum would double-count it; with no injection
    /// the decomposition is exactly two terms, which is the form §7 states.</para>
    ///
    /// <para><b>Then the negative, which is what gives the gate teeth.</b> One of the two component
    /// solves is re-run with its source stamped at a DIFFERENT pad — §9's "a second source stamped at
    /// the wrong node produces a plausible number, not an error" — and the sum must stop matching.
    /// Superposition is insensitive to WHERE a source sits, so a gate that moved the source in every
    /// solve at once would still pass; moving it in one is what reproduces the defect.</para>
    /// </summary>
    [Fact]
    public void TwoSourcesSumToTheTwoSourceSolve()
    {
        static RailDcResult Solve(double va, double vb, bool misplaceA = false)
        {
            var doc = new RailDocument { Name = "two sources" };
            var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
            rail.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "BT1", Pin = misplaceA ? "2" : "1" },
                OpenCircuitVoltageV = va,
                SeriesResistanceOhms = 0.2,
            });
            rail.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "BT2", Pin = "1" },
                OpenCircuitVoltageV = vb,
                SeriesResistanceOhms = 0.3,
            });
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
            doc.Rails.Add(rail);

            var run = RailDcRun.Run(new RailDcRequest
            {
                Document = doc,
                Technology = Board(35.0, 35.0),
                Shapes =
                [
                    Rect(Top, 0, 0, Mm(30), Mm(0.4)),
                    Rect(Bot, 0, 0, Mm(30), Mm(0.4)),
                ],
                Pads =
                [
                    new PdnPad("BT1", "1", "VDD", Mm(0.2), Mm(0.2), PdnPadSource.BoardNetlist),
                    new PdnPad("BT1", "2", "VDD", Mm(12.0), Mm(0.2), PdnPadSource.BoardNetlist),   // the WRONG pad, for the negative
                    new PdnPad("BT2", "1", "VDD", Mm(29.8), Mm(0.2), PdnPadSource.BoardNetlist),
                    new PdnPad("U1", "VDD", "VDD", Mm(18.0), Mm(0.2), PdnPadSource.BoardNetlist),
                ],
            });

            Assert.Null(run.Refusal);
            return run.Rails[0];
        }

        var both = Solve(3.7, 3.4);
        var aAlone = Solve(3.7, 0.0);
        var bAlone = Solve(0.0, 3.4);

        // Named, not indexed: a comparison that assumed two runs numbered their nodes identically
        // would silently compare different copper the first time an extraction reordered anything.
        static Dictionary<string, double> Field(RailDcResult r)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (node, v) in r.NodeVoltages)
                map[r.Netlist.Netlist.Nodes.NameOf(node)] = v;
            return map;
        }

        var f = Field(both);
        var fa = Field(aAlone);
        var fb = Field(bAlone);

        Assert.Equal(f.Count, fa.Count);
        Assert.Equal(f.Count, fb.Count);
        Assert.NotEmpty(f);

        double worst = 0;
        foreach (var (name, v) in f)
            worst = Math.Max(worst, Math.Abs(v - (fa[name] + fb[name])));

        Assert.True(worst < 1e-9, $"superposition held only to {worst:0.###e+0} V");

        // ── the negative, read at the PORT ─────────────────────────────────────────────────────
        //
        // Moving a source's pad moves the junctions the graph reading puts on the copper, so the two
        // netlists no longer share every node NAME — which is correct and is why the comparison here
        // is at the one node both of them certainly have, the load's own. That is also the node a
        // user would look at, and §9's defect is precisely that it carries a plausible number.
        double sum = aAlone.Ports[0].VoltageV + bAlone.Ports[0].VoltageV;
        Assert.Equal(both.Ports[0].VoltageV, sum, 9);

        var aMisplaced = Solve(3.7, 0.0, misplaceA: true);
        double wrong = aMisplaced.Ports[0].VoltageV + bAlone.Ports[0].VoltageV;

        Assert.True(Math.Abs(both.Ports[0].VoltageV - wrong) > 1e-3,
            $"a source stamped at the wrong node must break the sum: {both.Ports[0].VoltageV:0.######} V " +
            $"against {wrong:0.######} V");
    }

    /// <summary>
    /// §2.2: "the geometry is what decides how they share, which is the answer a hand calculation
    /// cannot give". So the share is a first-class number — and the arithmetic that holds it is KCL:
    /// whatever the geometry decides, the two sources between them deliver exactly the load current.
    /// </summary>
    [Fact]
    public void TheSourcesShareAndTheirCurrentsAreTheLoadCurrent()
    {
        var doc = new RailDocument { Name = "sharing" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.2,
        });
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT2", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.6,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.5,
        });
        doc.Rails.Add(rail);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(35.0, 35.0),
            Shapes = [Rect(Top, 0, 0, Mm(30), Mm(0.4)), Rect(Bot, 0, 0, Mm(30), Mm(0.4))],
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", Mm(0.2), Mm(0.2), PdnPadSource.BoardNetlist),
                new PdnPad("BT2", "1", "VDD", Mm(29.8), Mm(0.2), PdnPadSource.BoardNetlist),
                new PdnPad("U1", "VDD", "VDD", Mm(15.0), Mm(0.2), PdnPadSource.BoardNetlist),
            ],
        });

        Assert.Null(run.Refusal);
        var result = run.Rails[0];

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(0.5, result.Sources.Sum(s => s.CurrentA), 6);
        Assert.Equal(1.0, result.Sources.Sum(s => s.ShareOfTotal), 9);

        // 0.2 Ω against 0.6 Ω on an almost symmetric board: the lower-resistance branch carries more,
        // and the copper is what makes the split something other than 3:1.
        Assert.True(result.Sources[0].CurrentA > result.Sources[1].CurrentA);
        Assert.True(result.Sources[0].ShareOfTotal > 0.5);
    }

    // ── R-rail5-7 / R-rail5-9: the rail chain, in dependency order ─────────────────────────────

    /// <summary>
    /// §7's hand-solved two-rail ladder.
    ///
    /// <para><b>The board is built so the loop has a closed form.</b> The rail strip and the
    /// reference strip are the same width and thickness and run the same distance, so the loop is
    /// <c>2·R_s</c> — §4.1's own factor of two — and the whole ladder is arithmetic:</para>
    ///
    /// <code>
    ///   R_s   = ρ·L/(W·T) = 1.724138e-8 × 0.0195 / (0.5e-3 × 35e-6) = 19.2118 mΩ
    ///   loop  = 2 × 19.2118 mΩ                                      = 38.4237 mΩ
    ///   V(U2) = 3.7 − 0.5 × (0.1 + 0.0384237)                       = 3.630788 V
    /// </code>
    ///
    /// <para>3.630788 V is the number the second solve starts from, and the gate is that it is the
    /// UPSTREAM ANSWER and not the 3.7 V nominal.</para>
    ///
    /// <para><b>Then the assertion that catches a chain reading a nominal:</b> narrow the input
    /// rail's copper, and the OUTPUT rail's answer must move. A chain that read the nominal would
    /// pass the first assertion and fail this one.</para>
    /// </summary>
    [Fact]
    public void TheChainStartsFromTheUpstreamAnswerAndNotTheNominal()
    {
        static RailDcRunResult Ladder(double inputTraceWidthMm, double? minimumInputV = 3.25)
        {
            var doc = new RailDocument { Name = "ladder" };

            var vin = new RailSpec { Name = "VIN", ReferenceLayer = Bot };
            vin.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
                OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.1,
            });
            vin.Loads.Add(new RailLoad
            {
                Anchor = new RailPortAnchor { Refdes = "U2", Pin = "IN" },
                DcCurrentA = 0.5, MinimumInputVoltageV = minimumInputV,
            });

            var vout = new RailSpec { Name = "VOUT", ReferenceLayer = Bot };
            // No open-circuit voltage: this row states an IMPEDANCE and no level, and on a chained
            // rail the level is the upstream answer at U2's own input pin field (R-rail5-7).
            vout.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "U2", Pin = "OUT" },
                SeriesResistanceOhms = 0.05,
            });
            vout.Loads.Add(new RailLoad
            {
                Anchor = new RailPortAnchor { Refdes = "U3", Pin = "VDD" }, DcCurrentA = 0.2,
            });

            doc.Rails.Add(vin);
            doc.Rails.Add(vout);

            long w = Mm(inputTraceWidthMm);

            return RailDcRun.Run(new RailDcRequest
            {
                Document = doc,
                Technology = Board(35.0, 35.0),
                Shapes =
                [
                    Rect(Top, 0, 0, Mm(20), w),                  // VIN's own copper
                    Rect(Top, Mm(22), 0, Mm(42), Mm(0.5)),       // VOUT's, galvanically separate
                    Rect(Bot, 0, 0, Mm(42), Mm(0.5)),            // one reference, as a board has
                ],
                Pads =
                [
                    new PdnPad("BT1", "1", "VIN", Mm(0.25), w / 2, PdnPadSource.BoardNetlist),
                    new PdnPad("U2", "IN", "VIN", Mm(19.75), w / 2, PdnPadSource.BoardNetlist),
                    new PdnPad("U2", "OUT", "VOUT", Mm(22.25), Mm(0.25), PdnPadSource.BoardNetlist),
                    new PdnPad("U3", "VDD", "VOUT", Mm(41.75), Mm(0.25), PdnPadSource.BoardNetlist),
                ],
            });
        }

        var run = Ladder(0.5);
        Assert.Null(run.Refusal);
        Assert.Equal(["VIN", "VOUT"], run.Order);

        var vinResult = run.Rail("VIN")!;
        var voutResult = run.Rail("VOUT")!;

        // ── assertion 1: the number written down in advance ────────────────────────────────────
        double rSection = Section(0.0195, 0.5e-3, 35e-6);
        double expected = 3.7 - 0.5 * (0.1 + 2 * rSection);
        Assert.Equal(3.630788, expected, 6);                       // the comment's own arithmetic

        double measured = vinResult.Ports[0].VoltageV;
        Assert.InRange(measured, expected * 0.99, expected * 1.01);

        // ── and it is the UPSTREAM ANSWER, not the nominal ─────────────────────────────────────
        Assert.NotNull(voutResult.ChainedFrom);
        Assert.Equal("VIN", voutResult.ChainedFrom!.UpstreamRail);
        Assert.Equal("U2", voutResult.ChainedFrom.Refdes);
        Assert.Equal(measured, voutResult.ChainedFrom.VoltageV, 12);
        Assert.NotEqual(3.7, voutResult.ChainedFrom.VoltageV, 3);

        // R-rail5-9: the headroom finding, with BOTH numbers, and it is a pass here.
        var u2 = Assert.Single(vinResult.Regulators);
        Assert.Equal(3.25, u2.MinimumInputVoltageV);
        Assert.True(u2.Ok);
        Assert.Contains("3.25", u2.Describe(), StringComparison.Ordinal);       // what it needs
        Assert.Contains("3.63", u2.Describe(), StringComparison.Ordinal);       // what it sees
        Assert.Empty(run.RegulatorsWithNoMinimum);

        // ── assertion 2: change the input rail's COPPER, and the OUTPUT rail moves ─────────────
        var narrow = Ladder(0.25);
        Assert.Null(narrow.Refusal);

        double before = voutResult.Ports[0].VoltageV;
        double after = narrow.Rail("VOUT")!.Ports[0].VoltageV;

        Assert.True(before - after > 5e-3,
            $"narrowing the INPUT rail's copper must move the OUTPUT rail's answer; it moved " +
            $"{(before - after) * 1e3:0.###} mV");

        // ── R-rail5-9's other half: no minimum stated, so NO finding — and the absence is LISTED ─
        var unstated = Ladder(0.5, minimumInputV: null);
        Assert.Null(unstated.Refusal);

        var quiet = Assert.Single(unstated.Rail("VIN")!.Regulators);
        Assert.Null(quiet.MinimumInputVoltageV);
        Assert.Null(quiet.MarginV);
        Assert.Null(quiet.Ok);
        Assert.Equal(measured, quiet.InputVoltageV, 12);            // the drop is reported ALWAYS
        Assert.Empty(unstated.Rail("VIN")!.Findings);               // and the headroom is not
        Assert.Contains(unstated.RegulatorsWithNoMinimum, r => r.Refdes == "U2");
    }

    // ── R-rail5-8: a cycle is REFUSED, never iterated ──────────────────────────────────────────

    /// <summary>
    /// §9: solving the rails together is a different model, it needs exactly the data §8.2 records as
    /// frequently impossible to obtain, and "it would be entered by accident the first time someone
    /// asked for a cycle in the order to be supported". So the run refuses, names both rails and the
    /// refdes that closes the loop, and solves NOTHING — no iteration count, no relaxation, no
    /// partial answer.
    /// </summary>
    [Fact]
    public void ACycleInTheOrderIsRefusedAndNamesBothRails()
    {
        var doc = new RailDocument { Name = "cycle" };

        var a = new RailSpec { Name = "+3V3", ReferenceLayer = Bot };
        a.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "U3", Pin = "OUT" }, OpenCircuitVoltageV = 3.3 });
        a.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "IN" }, DcCurrentA = 0.1 });

        var b = new RailSpec { Name = "+1V8", ReferenceLayer = Bot };
        b.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "OUT" }, OpenCircuitVoltageV = 1.8 });
        b.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U3", Pin = "IN" }, DcCurrentA = 0.1 });

        doc.Rails.Add(a);
        doc.Rails.Add(b);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(35.0, 35.0),
            Shapes = [Rect(Top, 0, 0, Mm(10), Mm(0.4)), Rect(Bot, 0, 0, Mm(10), Mm(0.4))],
        });

        Assert.NotNull(run.Refusal);
        Assert.Empty(run.Rails);
        Assert.Contains("+3V3", run.Refusal, StringComparison.Ordinal);
        Assert.Contains("+1V8", run.Refusal, StringComparison.Ordinal);
        // The refdes named is the one that CLOSES the loop — the walk reports one actual cycle
        // rather than the whole stuck set, because "these rails could not be ordered" does not say
        // which row to change and "U3 is a load on '+1V8' and a source on '+3V3'" does.
        Assert.Contains("U3", run.Refusal, StringComparison.Ordinal);
    }

    // ── R-rail5-3 / R-rail5-4 / R-rail5-5: the ranked breakdown ────────────────────────────────

    /// <summary>
    /// §2.8's own table, built as a board: a 350 mΩ protection FET bridging the gap the copper leaves
    /// at its pads, and 50 mm of 0.3 mm 0.5 oz inner copper beyond it.
    ///
    /// <code>
    ///   R(FET)                                                          = 350 mΩ
    ///   R(run) = 1.724138e-8 × 0.0497 / (0.3e-3 × 17.5e-6)              = 163.2 mΩ
    /// </code>
    ///
    /// <para><b>R-rail5-5 is the assertion that the copper is near the TOP of the table.</b> Rev 2 of
    /// the design note assumed the copper was a rounding error and rev 3 exists to correct it: at
    /// 0.99 mΩ/square for 0.5 oz, 50 mm of 0.3 mm copper is 167 squares and therefore comparable with
    /// the series semiconductor. Nothing in the ranking special-cases it.</para>
    ///
    /// <para><b>R-rail5-3/R-rail5-4</b>: the rows sum to the total drop, the shares sum to one, and
    /// the copper rows name their LAYER — §2.6's worked example turns on exactly that.</para>
    /// </summary>
    [Theory]
    [InlineData(PdnModelKind.Fast)]
    [InlineData(PdnModelKind.Accurate)]
    public void TheBreakdownRanksTheFetFirstAndTheCopperSecond(PdnModelKind model)
    {
        var run = RailDcRun.Run(ReferenceBoard(model));

        Assert.Null(run.Refusal);
        var result = run.Rails[0];

        // ── the rows sum to the total drop, and the shares to one ──────────────────────────────
        double total = result.Ports[0].DropV!.Value;
        double summed = result.Breakdown.Sum(r => r.DropV);

        Assert.Equal(total, summed, 6);
        Assert.Equal(1.0, result.Breakdown.Sum(r => r.ShareOfTotal), 9);

        // ── ranked, and the copper is second ───────────────────────────────────────────────────
        var fet = result.Breakdown[0];
        var copper = result.Breakdown[1];

        Assert.Contains("Q1", fet.Label, StringComparison.Ordinal);
        Assert.Equal(0.350, fet.ResistanceOhms, 6);
        Assert.Equal(0.1, fet.CurrentA, 4);

        Assert.InRange(copper.ResistanceOhms, 0.155, 0.172);
        Assert.True(copper.ShareOfTotal > 0.30,
            $"the copper carried only {copper.ShareOfTotal:P1} of the drop; rev 3 of the note exists " +
            "because that number is not small");

        // ── R-rail5-4: the layer survives the aggregation ──────────────────────────────────────
        Assert.Contains("TOP", copper.Label, StringComparison.Ordinal);
        Assert.Contains(result.Breakdown, r => r.Label.Contains("BOT", StringComparison.Ordinal));

        // A mesh path is thousands of cell edges and this is a handful of rows — which is the whole
        // of R-rail5-4. The fast reading's own path is already a handful of elements, so the ratio
        // is asserted where it means something.
        Assert.True(result.Breakdown.Count < 12,
            $"the breakdown came back as {result.Breakdown.Count} rows; a breakdown nobody reads is " +
            "not a breakdown");

        if (model == PdnModelKind.Accurate)
            Assert.True(result.Netlist.Netlist.Components.Count > 50 * result.Breakdown.Count);
    }

    // ── R-rail5-2: the FIELD is complete, and its interpolation is stated once ─────────────────

    /// <summary>
    /// Every node the drop map can colour has a voltage. A node with no voltage is a HOLE in the
    /// picture, and a hole in a heat map reads as a value rather than as an absence.
    ///
    /// <para>And the field reads BETWEEN cells, because a cursor reads out the absolute voltage
    /// anywhere on the net (§2.4) — one rule, stated on <see cref="RailDcResult.VoltageAt"/>, so the
    /// window and the headless report cannot differ about it.</para>
    /// </summary>
    [Fact]
    public void EveryNodeTheMapCanColourHasAVoltage()
    {
        var run = RailDcRun.Run(ReferenceBoard(PdnModelKind.Accurate));
        Assert.Null(run.Refusal);
        var result = run.Rails[0];

        Assert.NotEmpty(result.Netlist.NodeCells);
        foreach (var (node, _) in result.Netlist.NodeCells)
            Assert.True(result.NodeVoltages.ContainsKey(node), $"node {node} has no voltage");

        // Between the source pad and the load pad, on the rail's own copper, the field falls
        // monotonically — and it is read at a coordinate rather than at a cell.
        double near = result.VoltageAt(Top, isReference: false, Mm(8), Mm(0.15))!.Value;
        double far = result.VoltageAt(Top, isReference: false, Mm(50), Mm(0.15))!.Value;

        Assert.True(near > far, $"the rail rose along the run: {near:0.#####} V then {far:0.#####} V");

        // Copper that is not there answers nothing, which is honest — there is no voltage to read.
        Assert.Null(result.VoltageAt(new LayerKey(99, 0), isReference: false, 0, 0));
    }

    // ── R-rail5-10: an observation port appears in the report AS OBSERVED ──────────────────────

    /// <summary>
    /// §2.2: a port with no stated current is not a load, and "the DC report lists it as observed
    /// rather than omitting it". Omitting it is the failure — a user who added a port and sees no row
    /// for it concludes the port did not take effect, and <i>not added</i> and <i>added with no
    /// current</i> must not look the same.
    /// </summary>
    [Fact]
    public void ThreeLoadsOneCurrentlessGiveThreeRowsAndTwoInjections()
    {
        var doc = new RailDocument { Name = "three loads" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.05,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.30 });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "VDD" } });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U3", Pin = "VDD" }, DcCurrentA = 0.05 });
        doc.Rails.Add(rail);

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(35.0, 35.0),
            Shapes = [Rect(Top, 0, 0, Mm(30), Mm(0.4)), Rect(Bot, 0, 0, Mm(30), Mm(0.4))],
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", Mm(0.2), Mm(0.2), PdnPadSource.BoardNetlist),
                new PdnPad("U1", "VDD", "VDD", Mm(10.0), Mm(0.2), PdnPadSource.BoardNetlist),
                new PdnPad("U2", "VDD", "VDD", Mm(20.0), Mm(0.2), PdnPadSource.BoardNetlist),
                new PdnPad("U3", "VDD", "VDD", Mm(29.8), Mm(0.2), PdnPadSource.BoardNetlist),
            ],
        });

        Assert.Null(run.Refusal);
        var result = run.Rails[0];

        Assert.Equal(3, result.Ports.Count);

        var observed = Assert.Single(result.Ports, p => p.IsObservationOnly);
        Assert.Equal("U2.VDD", observed.Name);
        Assert.Contains("observed", observed.Describe(), StringComparison.Ordinal);

        // It drew nothing and it is still measured somewhere real — between its neighbours' answers.
        Assert.InRange(observed.VoltageV, result.Ports[2].VoltageV, result.Ports[0].VoltageV);

        // Two injections, not three: an unstated current is never a defaulted zero.
        int injections = result.Netlist.Netlist.Components.Count(c => c.Model is CurrentToneSourceModel);
        Assert.Equal(2, injections);
    }

    // ── R-rail5-11: everything at 20 °C, said once, on the report ──────────────────────────────

    /// <summary>
    /// §2.4 is a scope statement as much as a setting: railRF computes at one stated temperature,
    /// says so, and does not pretend to be a thermal tool. One line, carrying the +0.39 %/K figure
    /// and the 85 °C example, and nothing more — no setting, no sweep, no derating.
    /// </summary>
    [Fact]
    public void TheReportSaysWhatTemperatureItIsAtAndNothingMore()
    {
        var run = RailDcRun.Run(ReferenceBoard(PdnModelKind.Fast));
        Assert.Null(run.Refusal);

        string line = run.Rails[0].TemperatureLine;
        Assert.Contains("20 °C", line, StringComparison.Ordinal);
        Assert.Contains("0.39", line, StringComparison.Ordinal);
        Assert.Contains("85 °C", line, StringComparison.Ordinal);
        Assert.Contains("not a thermal tool", line, StringComparison.Ordinal);

        Assert.Contains(run.Rails[0].Notes, n => n.Contains("0.39", StringComparison.Ordinal));
    }

    // ── §7 through the SOLVE: Fast against Accurate on the DROP ────────────────────────────────

    /// <summary>
    /// §2.9's fourth rule, seen where a user meets it. Brief 4 gates the two readings against each
    /// other on the RESISTANCE its own little solver measures; this is the same 5 % gate on the
    /// answer the report prints — the drop at the load — which is the number the rest of the tool
    /// is built on.
    /// </summary>
    [Fact]
    public void FastAndAccurateAgreeOnTheDropToFivePercent()
    {
        static double Drop(PdnModelKind model)
        {
            var doc = new RailDocument { Name = "straight" };
            var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
            rail.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
                OpenCircuitVoltageV = 3.7,
            });
            rail.Loads.Add(new RailLoad
            {
                Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.5,
            });
            doc.Rails.Add(rail);

            long w = Mm(0.3), l = Mm(40);
            var run = RailDcRun.Run(new RailDcRequest
            {
                Document = doc,
                Technology = Board(35.0, 35.0),
                Model = model,
                Shapes =
                [
                    Rect(Top, 0, 0, l, w),
                    Rect(Bot, 0, -Mm(0.35), l, w + Mm(0.35)),
                ],
                Pads =
                [
                    new PdnPad("BT1", "1", "VDD", Mm(0.1), w / 2, PdnPadSource.BoardNetlist),
                    new PdnPad("U1", "VDD", "VDD", l - Mm(0.1), w / 2, PdnPadSource.BoardNetlist),
                ],
            });

            Assert.Null(run.Refusal);
            return run.Rails[0].Ports[0].DropV!.Value;
        }

        double fast = Drop(PdnModelKind.Fast);
        double accurate = Drop(PdnModelKind.Accurate);

        Assert.True(fast > 0 && accurate > 0);
        Assert.InRange(fast / accurate, 0.95, 1.05);
    }

    // ── shared fixtures ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §2.8's table as a board: a battery, a gap the protection FET bridges, and 50 mm of 0.3 mm
    /// 0.5 oz inner copper to the load. <b>The gap is the point</b> — §2.8: "on imported artwork the
    /// copper stops at every pad, so the board is not electrically continuous until the user has said
    /// what bridges each gap."
    /// </summary>
    private static RailDcRequest ReferenceBoard(PdnModelKind model)
    {
        var doc = new RailDocument { Name = "the note's own board" };
        var rail = new RailSpec { Name = "VBAT", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.001,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.1,
        });
        doc.Rails.Add(rail);

        return new RailDcRequest
        {
            Document = doc,
            Technology = Board(17.5, 70.0),         // 0.5 oz inner rail, a heavy reference
            Model = model,
            Shapes =
            [
                Rect(Top, 0, 0, Mm(3.5), Mm(0.3)),           // the battery's own copper
                Rect(Top, Mm(6), 0, Mm(56), Mm(0.3)),        // 50 mm of 0.3 mm run beyond the FET
                Rect(Bot, Mm(-1), Mm(-3), Mm(57), Mm(3)),    // the reference
            ],
            Pads =
            [
                new PdnPad("BT1", "1", "VBAT", Mm(0.15), Mm(0.15), PdnPadSource.BoardNetlist),
                new PdnPad("Q1", "1", "VBAT", Mm(3.35), Mm(0.15), PdnPadSource.BoardNetlist),
                new PdnPad("Q1", "2", "VBAT", Mm(6.15), Mm(0.15), PdnPadSource.BoardNetlist),
                new PdnPad("U1", "VDD", "VBAT", Mm(55.85), Mm(0.15), PdnPadSource.BoardNetlist),
            ],
            SeriesElements =
            [
                new PdnSeriesElement(
                    "Q1",
                    new RailPortAnchor { Refdes = "Q1", Pin = "1" },
                    new RailPortAnchor { Refdes = "Q1", Pin = "2" },
                    0.350,
                    "the part library's on-resistance"),
            ],
        };
    }
}
