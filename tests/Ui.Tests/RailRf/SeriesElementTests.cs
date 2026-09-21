// ================================================================
//  SeriesElementTests.cs — brief-railrf-25-series-element.md §5
//
//  The rail gains a second node. Everything under test is src/Design/RailRf and
//  src/Design/Layout/Pdn, both below the UI firewall — no window, no app host, no Avalonia.
//
//  ── THE BRIEF NAMES tests/Design.Tests AND THERE IS NO SUCH PROJECT ──────────────────────────
//
//  Every railRF test in this repository lives in tests/Ui.Tests/RailRf/, including the ones that
//  touch nothing but src/Design — PdnDcSolveTests, PdnImpedanceTests, PdnMeshExtractorTests. This
//  file joins them rather than standing up an eighth test project for one brief.
//
//  ── THE FIRST TEST IS THE ONLY ONE THAT MATTERS IF THE OTHERS PASS AND IT FAILS ──────────────
//
//  Gate 1 is a CLOSED-FORM oracle written out by hand — two ports of a source/series/shunt ladder,
//  from the impedances the document states, with nothing from circuitRF in it. That is the test
//  that says the second node exists at all: every other gate here would pass just as happily on a
//  rail that is still one node, because a refusal, a note and a partition are all arithmetic-free.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SeriesElementTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // ── the board, shared with the DC gates ──────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private const double CopperSigma = 5.8e7;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static Technology Board()
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static Path64 Box(long x1, long y1, long x2, long y2) =>
        [new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)];

    // ── the parts ────────────────────────────────────────────────────────────

    private const double MountingH = 1e-9;

    private static PartLibraryRow Cap1uRow() => new()
    {
        PartNumber              = "PN-1U",
        DielectricClass         = "X7R",
        CapacitanceFarads       = 1e-6,
        SelfResonantFrequencyHz = 5.31e6,
        EsrOhms                 = 8e-3,
    };

    private static PartLibraryRow Cap100nRow() => new()
    {
        PartNumber              = "PN-100N",
        DielectricClass         = "X7R",
        CapacitanceFarads       = 100e-9,
        SelfResonantFrequencyHz = 16.0e6,
        EsrOhms                 = 5e-3,
    };

    internal static PartLibrary Library()
    {
        var lib = new PartLibrary();
        lib.Rows.AddRange([Cap1uRow(), Cap100nRow()]);
        return lib;
    }

    private static RailPart Cap(string refdes, string partNumber, RailSection side) => new()
    {
        Refdes = refdes,
        PartNumber = partNumber,
        MountingInductanceHenries = MountingH,
        Side = side,
    };

    /// <summary>The ferrite: a pure 1 Ω over frequency, so the oracle's own arithmetic is short and
    /// the difference between the two ports is exactly one number.</summary>
    internal static RailPart Ferrite(
        double seriesOhms = 1.0, double? seriesHenries = null, double? dcrOhms = 0.060) => new()
    {
        Refdes = "FB1",
        PartNumber = "PN-FERRITE",
        Connection = RailPartConnection.Series,
        TerminalA = new RailPortAnchor { Refdes = "FB1", Pin = "1" },
        TerminalB = new RailPortAnchor { Refdes = "FB1", Pin = "2" },
        SeriesResistanceOhms = seriesOhms,
        SeriesInductanceHenries = seriesHenries,
        DcResistanceOhms = dcrOhms,
    };

    private const double SourceOhms = 1.0;
    private const double SourceHenries = 1e-6;

    private static RailSourceModel SourceModel() =>
        new(0, "BT1", RailSourceBasis.Rl, SourceOhms, SourceHenries, 3.6);

    // ══ GATE 1 — THE CLOSED-FORM ORACLE ══════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> Source R-L, one series R, two shunt caps on the DOWNSTREAM side, and an
    /// observation port on each side. The two curves differ — by the series R — at every frequency,
    /// and both match an oracle written out by hand from the impedances the document states.
    /// </summary>
    /// <remarks>
    /// <b>The oracle is built here and not from another railRF path</b>, which is the brief's own
    /// instruction and the reason this test is worth anything: the driving-point impedance at the
    /// upstream node is <c>Zs ∥ (Zseries + Zdown)</c> and at the downstream node it is
    /// <c>Zdown ∥ (Zseries + Zs)</c>, where <c>Zdown</c> is the two capacitors in parallel. Every
    /// term comes out of the document — R, L, C, ESR and the mounting loop — and none of it comes
    /// out of circuitRF.
    ///
    /// <para><b>A rail that is still ONE node fails this by a mile and not by a rounding error</b>:
    /// with the two nodes merged both ports read <c>Zs ∥ Zdown</c>, and at 1 MHz that is 0.16 Ω
    /// against the 0.88 Ω the upstream port actually sees.</para>
    /// </remarks>
    [Fact]
    public void Gate1_TwoPortsOfOneRailDifferByTheSeriesElement_AgainstAClosedForm()
    {
        var rail = TypedRail();
        var result = Sweep(rail);

        Assert.Null(result.Refusal);
        Assert.Equal(2, result.Ports.Count);

        double worstUp = 0, worstDown = 0, leastRatio = double.MaxValue;

        for (int i = 0; i < result.FrequenciesHz.Length; i++)
        {
            double f = result.FrequenciesHz[i];
            double w = 2 * Math.PI * f;

            // Every term from the document, by hand.
            var zs = new Complex(SourceOhms, w * SourceHenries);
            var zseries = new Complex(1.0, 0.0);
            var z1u = Cap(w, 1e-6, 5.31e6, 8e-3);
            var z100n = Cap(w, 100e-9, 16.0e6, 5e-3);
            var zdown = Parallel(z1u, z100n);

            double up = Parallel(zs, zseries + zdown).Magnitude;
            double down = Parallel(zdown, zseries + zs).Magnitude;

            worstUp = Math.Max(worstUp, Math.Abs(result.Ports[0].MagnitudeOhms[i] - up) / up);
            worstDown = Math.Max(worstDown, Math.Abs(result.Ports[1].MagnitudeOhms[i] - down) / down);

            double ratio = result.Ports[0].MagnitudeOhms[i] / result.Ports[1].MagnitudeOhms[i];
            leastRatio = Math.Min(leastRatio, Math.Max(ratio, 1.0 / ratio));
        }

        _output.WriteLine(
            $"worst relative error: upstream {worstUp:0.###e+00}, downstream {worstDown:0.###e+00}; " +
            $"the two ports are never closer than a factor of {leastRatio:0.####}");

        Assert.True(worstUp < 1e-9, $"upstream port is off the closed form by {worstUp:0.###e+00}");
        Assert.True(worstDown < 1e-9, $"downstream port is off the closed form by {worstDown:0.###e+00}");

        // R-rail25-4b's own condition, measured rather than asserted from the note: the two ports
        // genuinely differ, at EVERY frequency and not merely somewhere.
        Assert.True(leastRatio > 1.0001, $"the two ports coincide somewhere (ratio {leastRatio})");

        static Complex Cap(double w, double c, double f0, double esr)
        {
            double lPackage = 1.0 / (Math.Pow(2 * Math.PI * f0, 2) * c);
            return new Complex(esr, w * (lPackage + MountingH) - 1.0 / (w * c));
        }

        static Complex Parallel(Complex a, Complex b) => a * b / (a + b);
    }

    // ══ GATE 9 — THE SAME-CURVE NOTE IS CONDITIONAL ══════════════════════════════════════════

    /// <summary>
    /// <b>Gate 9, and the brief says this one will be missed.</b> The note is correct today and
    /// becomes a lie the moment a series element exists — and nothing fails when a lie is printed,
    /// which is exactly why it needs a gate of its own.
    /// </summary>
    [Fact]
    public void Gate9_TheSameCurveNote_IsPresentWithoutASeriesElementAndAbsentWithOne()
    {
        const string claim = "reads the same curve";

        var without = Sweep(TypedRail(withSeriesElement: false));
        Assert.Contains(without.Notes, n => n.Contains(claim, StringComparison.Ordinal));

        var with = Sweep(TypedRail());
        Assert.DoesNotContain(with.Notes, n => n.Contains(claim, StringComparison.Ordinal));

        // And what replaces it says the opposite, by name.
        Assert.Contains(with.Notes, n => n.Contains("DIFFERENT curves", StringComparison.Ordinal));
    }

    // ══ R-rail25-1c — THE HONEST SENTENCE ════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-rail25-1c.</b> An R-L-modelled bead carries the bias caveat on the RESULT, and a
    /// measured curve makes it go away.
    /// </summary>
    [Fact]
    public void TheBiasCaveat_RidesTheResultForAnRlAndGoesAwayForAMeasuredCurve()
    {
        var rl = Sweep(TypedRail());
        Assert.Contains(rl.Warnings, w => w.Contains("BIAS-DEPENDENT", StringComparison.Ordinal));

        // A file the resolver read: two points of flat 1 Ω, which is the same element measured.
        var measured = new RailMeasuredPart(
            "FB1.s2p", [1e3, 1e9], [new Complex(1, 0), new Complex(1, 0)], null);

        var rail = TypedRail();
        int at = rail.Parts.IndexOf(rail.SeriesElement!);

        // The ROW is what says which model this element has — a supplied sweep with the R-L still
        // on the row is the two-models-stated case RailPart.Refusal already refuses.
        rail.Parts[at] = rail.Parts[at] with
        {
            TouchstoneRef = "FB1.s2p",
            SeriesResistanceOhms = null,
            SeriesInductanceHenries = null,
        };

        var result = PdnSweep.Run(SweepRequest(rail, RailSeriesModel.Of(rail.Parts[at], measured)));

        Assert.Null(result.Refusal);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("BIAS-DEPENDENT", StringComparison.Ordinal));
    }

    // ══ GATE 5 — THE TYPED ROUTE, WITH NO ARTWORK ════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5 (R-rail25-2d).</b> With no artwork the sides are the rows' own, downstream by
    /// default, and the partition says so rather than looking like a measurement.
    /// </summary>
    [Fact]
    public void Gate5_WithNoArtwork_TheSidesAreTheRowsOwnAndTheAnswerSaysSo()
    {
        var partition = RailSeriesPartition.Typed(TypedRail());

        Assert.Null(partition.Refusal);
        Assert.False(partition.FromArtwork);
        Assert.Equal(RailSection.Upstream, partition.PartSection("C1"));
        Assert.Equal(RailSection.Downstream, partition.PartSection("C2"));
        Assert.Equal(RailSection.Downstream, partition.PartSection("C3"));
        Assert.Equal(RailSection.Upstream, partition.LoadSection(0));
        Assert.Equal(RailSection.Downstream, partition.LoadSection(1));

        // A source is upstream of the element by definition and there is no field for it.
        Assert.Equal(RailSection.Upstream, partition.SourceSection(0));

        // A part row that states nothing is DOWNSTREAM, which is where decoupling goes.
        Assert.Equal(RailSection.Downstream, new RailPart { Refdes = "C9" }.Side);

        Assert.Contains(partition.Notes, n => n.Contains("no artwork", StringComparison.Ordinal));
    }

    // ══ GATE 2 / 3 — THE PARTITION COMES OFF THE ARTWORK ═════════════════════════════════════

    /// <summary>
    /// <b>Gate 2 (R-rail25-2a) and gate 3 (R-rail25-2b), on one board and its bridged twin.</b>
    /// </summary>
    /// <remarks>
    /// <b>The two halves are one test because they are one measurement</b>: the same board, the
    /// same walk, the same element — and the only difference is a rectangle of copper joining the
    /// two sides. Separating them would let the bridged case pass against a fixture that never
    /// separated in the first place, which is the failure mode of a refusal test.
    ///
    /// <para><b>This calls <c>Regions.Walk</c> directly</b> rather than running an
    /// extraction, because the walk IS the input the partition takes and this test is about what
    /// the partition does with it.</para>
    /// </remarks>
    [Fact]
    public void Gate2And3_TheSidesComeOffTheArtwork_AndABridgedElementIsRefusedByName()
    {
        var rail = ArtworkRail();
        var pads = ArtworkPads();

        // ── as placed: two pieces of rail copper with a gap the ferrite bridges ───────────────
        var separated = Walk(bridged: false);
        var partition = RailSeriesPartition.FromArtworkRegions(rail, separated, pads);

        Assert.Equal(2, separated.Power.Count);
        Assert.Null(partition.Refusal);
        Assert.True(partition.FromArtwork);

        // Exactly as placed: C1 is on the battery's copper and C2/C3 are beyond the ferrite.
        Assert.Equal(RailSection.Upstream, partition.PartSection("C1"));
        Assert.Equal(RailSection.Downstream, partition.PartSection("C2"));
        Assert.Equal(RailSection.Downstream, partition.PartSection("C3"));
        Assert.Equal(RailSection.Upstream, partition.SourceSection(0));
        Assert.Equal(RailSection.Downstream, partition.LoadSection(0));

        // Nothing typed any of that — every row above states the DEFAULT side, and C1's answer is
        // the opposite of what it states. That is the whole claim of R-rail25-2a.
        Assert.All(rail.Parts, p => Assert.Equal(RailSection.Downstream, p.Side));

        // ── the same board with copper around the element ────────────────────────────────────
        var bridged = RailSeriesPartition.FromArtworkRegions(rail, Walk(bridged: true), pads);

        Assert.NotNull(bridged.Refusal);
        Assert.Contains("FB1", bridged.Refusal);
        Assert.Contains("BRIDGED", bridged.Refusal, StringComparison.Ordinal);
        _output.WriteLine(bridged.Refusal);

        // And the RUN refuses on it rather than sweeping a circuit the board is not.
        var refused = PdnSweep.Run(SweepRequest(rail, RailSeriesModel.Of(rail.SeriesElement!), bridged));
        Assert.Equal(bridged.Refusal, refused.Refusal);
        Assert.Null(refused.Data);
    }

    // ══ GATE 4 — A SECOND SERIES ELEMENT ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4 (R-rail25-2c).</b> Refused BY NAME, on the document, so every path gets it — the
    /// window, the CLI's own <c>check</c>, the DC run and the sweep.
    /// </summary>
    [Fact]
    public void Gate4_ASecondSeriesElement_IsRefusedByNameWithWhatToDo()
    {
        var rail = TypedRail();
        rail.Parts.Add(Ferrite() with { Refdes = "FB2" });

        string? refusal = rail.Refusal();

        Assert.NotNull(refusal);
        Assert.Contains("'FB1'", refusal);
        Assert.Contains("'FB2'", refusal);
        Assert.Contains("rail of its own", refusal);
        _output.WriteLine(refusal);

        // The sweep is one of the paths that gets it, rather than a second copy of the rule.
        Assert.Equal(refusal, PdnSweep.Run(SweepRequest(rail, null)).Refusal);
    }

    // ══ GATES 6, 7, 8 — THE DC ANSWER ════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 6 (R-rail25-3a) and gate 7 (R-rail25-3b).</b> A series DCR is a row of the ranked
    /// breakdown, and an UNSTATED one makes the total a stated lower bound rather than a number
    /// that quietly omits the largest term after the source.
    /// </summary>
    [Fact]
    public void Gate6And7_TheSeriesDcrIsARankedBreakdownRow_AndAnUnstatedOneIsALowerBound()
    {
        var stated = RailDcRun.Run(DcRequest(dcrOhms: 0.350));
        Assert.Null(stated.Refusal);

        var rows = stated.Rails[0].Breakdown;
        var ferrite = rows.FirstOrDefault(r => r.Label.Contains("FB1", StringComparison.Ordinal));

        Assert.NotNull(ferrite);
        Assert.Equal(0.350, ferrite.ResistanceOhms, 1e-6);

        // RANKED WITH EVERYTHING ELSE, which is §4.3's "elements, never annotations": at 350 mΩ
        // against a millimetre of 35 µm copper it is the largest term after the source, and the
        // table is sorted by drop.
        _output.WriteLine(string.Join("\n", rows.Select(
            r => $"{r.Label}: {r.ResistanceOhms * 1e3:0.###} mΩ, {r.DropV * 1e3:0.###} mV, " +
                 $"{r.ShareOfTotal:P1}")));
        Assert.Equal(rows.OrderByDescending(r => r.DropV).First().Label, ferrite.Label);

        // ── unstated is not zero ─────────────────────────────────────────────────────────────
        var unstated = RailDcRun.Run(DcRequest(dcrOhms: null));
        Assert.Null(unstated.Refusal);

        Assert.Contains(unstated.Rails[0].Findings,
                        f => f.Contains("LOWER BOUND", StringComparison.Ordinal));
        Assert.DoesNotContain(stated.Rails[0].Findings,
                              f => f.Contains("LOWER BOUND", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Gate 8 (R-rail25-3c).</b> Unmounting a series element OPENS the rail, and both answers
    /// refuse with that reason rather than solving an open circuit. R-rail23-1e defers here.
    /// </summary>
    [Fact]
    public void Gate8_UnmountingASeriesElement_IsRefusedByBothAnswers()
    {
        var dc = RailDcRun.Run(DcRequest(dcrOhms: 0.350, mounted: false));
        Assert.NotNull(dc.Refusal);
        Assert.Contains("OPENS the rail", dc.Refusal, StringComparison.Ordinal);
        _output.WriteLine(dc.Refusal);

        var rail = TypedRail();
        var element = rail.SeriesElement!;
        rail.Parts[rail.Parts.IndexOf(element)] = element with { Mounted = false };

        var sweep = PdnSweep.Run(SweepRequest(rail, RailSeriesModel.Of(rail.Parts.Last())));
        Assert.NotNull(sweep.Refusal);
        Assert.Contains("OPENS the rail", sweep.Refusal, StringComparison.Ordinal);

        // And it is NOT the ordinary unmount: a depopulated SHUNT part is still reported and swept.
        var shunt = TypedRail();
        shunt.Parts[0] = shunt.Parts[0] with { Mounted = false };
        var still = Sweep(shunt);
        Assert.Null(still.Refusal);
        Assert.Contains(still.Notes, n => n.Contains("UNMOUNTED", StringComparison.Ordinal));
    }

    // ══ GATE 10 — NOTHING THAT HAD NO SERIES ELEMENT MOVED ═══════════════════════════════════

    /// <summary>
    /// <b>Gate 10.</b> A rail with no series element produces the same answer, bit for bit, whether
    /// or not brief 25's two new inputs are supplied.
    /// </summary>
    /// <remarks>
    /// <b>This is the strongest form of the gate that can be run in this repository, and it is not
    /// the form the brief asks for.</b> The brief asks for the shipped example run BEFORE and AFTER
    /// the change and compared bit for bit — which needs the pre-change binary, and nothing here
    /// has one. What is gated instead is that the new code path is INERT: the same rail, the same
    /// parts, the same sources, with <c>Series</c>/<c>Partition</c> null and then with a partition
    /// of a rail that has no element, give byte-identical <c>Z</c> cubes. That is what would break
    /// if the second node, the per-port node or the new notes leaked into a document that has no
    /// series element in it.
    /// </remarks>
    [Fact]
    public void Gate10_ARailWithNoSeriesElement_IsUnchangedByThisBrief()
    {
        var rail = TypedRail(withSeriesElement: false);

        var bare = PdnSweep.Run(SweepRequest(rail, null));
        var withPartitionSupplied = PdnSweep.Run(
            SweepRequest(rail, null, RailSeriesPartition.None(rail)));

        Assert.Null(bare.Refusal);
        Assert.Null(withPartitionSupplied.Refusal);

        var a = bare.Data!["Z"].ComplexValues;
        var b = withPartitionSupplied.Data!["Z"].ComplexValues;

        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(a[i] == b[i], $"Z differs at {i}");

        Assert.Equal(bare.Notes, withPartitionSupplied.Notes);
        Assert.Equal(bare.Warnings, withPartitionSupplied.Warnings);
    }

    /// <summary>
    /// The same claim on the FILE: a <c>.crail</c> that states nothing about a series element reads
    /// back stating nothing, and every part row still means what it meant.
    /// </summary>
    [Fact]
    public void Gate10_AnOlderDocument_ReadsBackAsShuntAndDownstream()
    {
        var part = RailDocumentIo.Deserialize(
            """
            { "Rails": [ { "Name": "VBAT",
                           "Parts": [ { "Refdes": "C1", "PartNumber": "PN-1U" } ] } ] }
            """).Rails[0].Parts[0];

        Assert.Equal(RailPartConnection.Shunt, part.Connection);
        Assert.Equal(RailSection.Downstream, part.Side);
        Assert.Null(part.TerminalA);
        Assert.Null(part.DcResistanceOhms);

        // And the round trip of a rail that HAS one keeps every field.
        var doc = new RailDocument { Name = "with a ferrite" };
        var rail = new RailSpec { Name = "VBAT" };
        rail.Parts.Add(Ferrite(seriesOhms: 600, seriesHenries: 1.2e-6, dcrOhms: 0.060));
        rail.Parts.Add(Cap("C1", "PN-1U", RailSection.Upstream));
        doc.Rails.Add(rail);

        var back = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(doc)).Rails[0];
        var ferrite = back.SeriesElement!;

        Assert.Equal("FB1", ferrite.Refdes);
        Assert.Equal(600, ferrite.SeriesResistanceOhms!.Value, 1e-9);
        Assert.Equal(1.2e-6, ferrite.SeriesInductanceHenries!.Value, 1e-15);
        Assert.Equal(0.060, ferrite.DcResistanceOhms!.Value, 1e-9);
        Assert.Equal("FB1", ferrite.TerminalA!.Refdes);
        Assert.Equal("2", ferrite.TerminalB!.Pin);
        Assert.Equal(RailSection.Upstream, back.Parts[1].Side);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The P1 rail of gate 1: no artwork, sides stated. C1 upstream, C2 and C3 downstream, one
    /// observation port on each side.
    /// </summary>
    private static RailSpec TypedRail(bool withSeriesElement = true)
    {
        var rail = new RailSpec
        {
            Name = "VDD_CORE",
            Band = new RailBand(1e3, 2e8, 401, true),
        };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.6,
            SeriesResistanceOhms = SourceOhms,
            SeriesInductanceHenries = SourceHenries,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "TP1" }, Side = RailSection.Upstream,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, Side = RailSection.Downstream,
        });

        // GATE 1's oracle has the two capacitors DOWNSTREAM and nothing upstream but the source, so
        // the upstream node's own answer is the source against the element and the bank behind it.
        // C1 is the upstream row of gate 5's typed partition and it is UNMOUNTED there rather than
        // in the oracle, which is why the oracle's rail carries only C2 and C3 as capacitance.
        if (withSeriesElement) rail.Parts.Add(Ferrite());
        rail.Parts.Add(Cap("C2", "PN-1U", RailSection.Downstream));
        rail.Parts.Add(Cap("C3", "PN-100N", RailSection.Downstream));
        rail.Parts.Insert(0, Cap("C1", "PN-1U", RailSection.Upstream) with { Mounted = false });
        return rail;
    }

    private static PdnSweepRequest SweepRequest(
        RailSpec rail, RailSeriesModel? series, RailSeriesPartition? partition = null) =>
        new()
        {
            Rail = rail,
            Parts = new RailPartResolver(Library()).ResolveAll(rail.Parts, railVoltageV: 3.6),
            Sources = [SourceModel()],
            Series = series,
            Partition = partition ?? (rail.SeriesElement is null
                                          ? RailSeriesPartition.None(rail)
                                          : RailSeriesPartition.Typed(rail)),
            RankRemovals = false,
        };

    private static PdnSweepResult Sweep(RailSpec rail) =>
        PdnSweep.Run(SweepRequest(
            rail, rail.SeriesElement is { } e ? RailSeriesModel.Of(e) : null));

    // ── the artwork gates' board ─────────────────────────────────────────────

    /// <summary>
    /// Two pieces of rail copper with a 2.5 mm gap the ferrite bridges, a reference plane under
    /// both, and five pads. <b>Every part row states the DEFAULT side</b>, so an answer that agrees
    /// with the placement is a measurement rather than a restatement of the document.
    /// </summary>
    private static RailSpec ArtworkRail()
    {
        var rail = new RailSpec { Name = "VBAT", NetName = "VBAT", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 0.05,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.35,
        });
        rail.Parts.Add(Ferrite());
        rail.Parts.Add(Cap("C1", "PN-1U", RailSection.Downstream));
        rail.Parts.Add(Cap("C2", "PN-1U", RailSection.Downstream));
        rail.Parts.Add(Cap("C3", "PN-100N", RailSection.Downstream));
        return rail;
    }

    private static IReadOnlyList<PlacedPin> ArtworkPads() =>
    [
        new PlacedPin("BT1", "1", "VBAT", Mm(0.5), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("C1", "1", "VBAT", Mm(2.0), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("FB1", "1", "VBAT", Mm(3.5), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("FB1", "2", "VBAT", Mm(6.5), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("C2", "1", "VBAT", Mm(8.0), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("C3", "1", "VBAT", Mm(10.0), Mm(0.15), PinSource.BoardNetlist),
        new PlacedPin("U1", "VDD", "VBAT", Mm(13.5), Mm(0.15), PinSource.BoardNetlist),
    ];

    private static IReadOnlyList<LayoutShape> ArtworkShapes(bool bridged) =>
    [
        // 0.3 mm of rail, which the FAST model prices as a trace — a 1 mm-wide pour would be
        // classified as spreading and the closed form refuses to put a number on one.
        Rect(Top, 0, 0, Mm(4), Mm(0.3)),                                   // the source's side
        Rect(Top, Mm(6), 0, Mm(14), Mm(0.3)),                              // beyond the ferrite
        .. bridged ? new LayoutShape[] { Rect(Top, Mm(4), Mm(0.1), Mm(6), Mm(0.2)) } : [],
        Rect(Bot, Mm(-1), Mm(-2), Mm(15), Mm(2)),                          // the reference
    ];

    private static PdnRailRegionSet Walk(bool bridged)
    {
        var top = new Paths64 { Box(0, 0, Mm(4), Mm(0.3)), Box(Mm(6), 0, Mm(14), Mm(0.3)) };
        if (bridged) top.Add(Box(Mm(4), Mm(0.1), Mm(6), Mm(0.2)));

        return Regions.Walk(
            new Dictionary<LayerKey, Paths64>
            {
                [Top] = Clipper.Union(top, LayoutClipper.Rule),
                [Bot] = [Box(Mm(-1), Mm(-2), Mm(15), Mm(2))],
            },
            Board(),
            [.. ArtworkPads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
            railNet: "VBAT",
            referenceLayer: Bot,
            referenceNet: null,
            extraRailSeeds: []);
    }

    /// <param name="withElement">False drops the ferrite row AND joins the two pieces of copper,
    /// which is the same board without one — the control for R-rail25-4c. Dropping the row alone
    /// would leave a rail in two halves with nothing bridging them, which is a different
    /// board.</param>
    internal static RailDcRequest DcRequest(
        double? dcrOhms, bool mounted = true, bool withElement = true)
    {
        var doc = new RailDocument { Name = "a rail with a ferrite in it" };
        var rail = ArtworkRail();

        if (withElement) rail.Parts[0] = Ferrite(dcrOhms: dcrOhms) with { Mounted = mounted };
        else rail.Parts.RemoveAt(0);

        doc.Rails.Add(rail);

        return new RailDcRequest
        {
            Document = doc,
            Technology = Board(),
            Shapes = ArtworkShapes(bridged: !withElement),
            Pads = ArtworkPads(),
            NetPoints = [.. ArtworkPads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
        };
    }
}
