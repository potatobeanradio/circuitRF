// ================================================================
//  PadLandLayerTests.cs — a pad is on its own land's layer, and one rail's refusal is its own
//
//  Both found reviewing a designer's two-rail workspace before a release (2026-09-23). The board:
//  a 0 Ω link on top copper, and the rail's own trace on the far layer running under it.
//
//  1. Series terminals, and every other anchor the DC assembly attaches, were matched against
//     "every rail node under this XY". The link's two top pads both sat over the far-layer trace, so
//     the partition read it as BRIDGED (and the sweep refused), a part beside it that is not on the
//     path read as the bridge, and the DC netlist tied each pad to the trace under it — a short
//     around the series element. Pads now attach on their land's layer, as the walk already seeds.
//  2. With that fixed, the fast model named the series element's own pad land as the spreading
//     copper the rail "only" reaches its load through. A series terminal lands its piece exactly as
//     a source or a load does (R-rail29-1).
//  3. The first refused rail refused the whole document, so work on the second showed only the
//     first one's sentence.
//
//  Everything under test is src/Design — no window, no app host.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PadLandLayerTests(ITestOutputHelper output)
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Mid = new(2, 0);
    private static readonly LayerKey Bot = new(3, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);
    private const double CopperSigma = 5.8e7;

    private const double DcrOhms = 0.350;
    private const double LoadA = 0.35;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>TOP carries the rail, MID carries a stub of the same rail, BOT is the reference. The
    /// via spans TOP → MID only, so it never touches the reference.</summary>
    private static Technology Board()
    {
        var tech = new Technology { Name = "pad land board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "C1", ThicknessDbu = Mm(0.4), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MID",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Mid],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "C2", ThicknessDbu = Mm(0.4), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(20),
                SpanFromLayer = "TOP", SpanToLayer = "MID",
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    /// <summary>
    /// Two top pieces with a gap FB1 bridges, and a MID stub of the far piece running under BOTH of
    /// FB1's pads, joined to the far piece by one via. The stub is a dead end: no current flows in
    /// it, so the right answer is the one the board would give without it.
    /// </summary>
    private static IReadOnlyList<LayoutShape> Shapes() =>
    [
        Rect(Top, 0, 0, 4, 0.3),
        Rect(Top, 6, 0, 14, 0.3),
        Rect(Mid, 3, 0, 7.4, 0.3),
        Via(7.2),
        Rect(Bot, -1, -3, 15, 3),
    ];

    /// <summary>
    /// The same rail routed on MID, with FB1 on two square TOP lands that are islands of their own,
    /// each reached through a via in the pad — the shape of the field board's jumper. Each land holds
    /// one terminal and is read as spreading copper.
    /// </summary>
    private static IReadOnlyList<LayoutShape> ViaInPadShapes() =>
    [
        Rect(Mid, 0, 0, 3.2, 0.3),
        Rect(Top, 2.7, -0.65, 4.3, 0.95),
        Via(3.0),
        Rect(Top, 5.7, -0.65, 7.3, 0.95),
        Via(7.0),
        Rect(Mid, 6.8, 0, 14, 0.3),
        Rect(Bot, -1, -3, 15, 3),
    ];

    private static ViaShape Via(double xMm) => new()
    {
        Layer = ViaLayer, LandingLayer = Top,
        X = Mm(xMm), Y = Mm(0.15), DrillSize = Mm(0.2), PadSize = Mm(0.3),
    };

    private static PlacedPin Pad(string refdes, string pin, double xMm, LayerKey land) =>
        new PlacedPin(refdes, pin, "VDD", Mm(xMm), Mm(0.15), PinSource.BoardNetlist) { Layer = land };

    /// <param name="ends">The layer the source and load pads are on — TOP, or MID for the
    /// via-in-pad board, whose rail is routed there.</param>
    private static IReadOnlyList<PlacedPin> Pads(LayerKey ends) =>
    [
        Pad("BT1", "1", 0.5, ends),
        Pad("FB1", "1", 3.5, Top),
        Pad("FB1", "2", 6.5, Top),
        Pad("U1", "VDD", 13.5, ends),
        Pad("U2", "VDD", 10.0, ends),
    ];

    private static RailSpec Rail(string name = "VDD", bool withFerrite = true, string sourceRefdes = "BT1",
                                 string sourcePin = "1", string loadRefdes = "U1")
    {
        var rail = new RailSpec { Name = name, ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = sourceRefdes, Pin = sourcePin },
            OpenCircuitVoltageV = 3.3,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = loadRefdes, Pin = "VDD" }, DcCurrentA = LoadA,
        });
        if (withFerrite)
            rail.Parts.Add(new RailPart
            {
                Refdes = "FB1",
                Connection = RailPartConnection.Series,
                TerminalA = new RailPortAnchor { Refdes = "FB1", Pin = "1" },
                TerminalB = new RailPortAnchor { Refdes = "FB1", Pin = "2" },
                DcResistanceOhms = DcrOhms,
            });
        return rail;
    }

    private static RailDcRunResult Run(RailDocument doc, bool viaInPad = false) => RailDcRun.Run(new RailDcRequest
    {
        Document = doc,
        Technology = Board(),
        DbuPerMicron = DbuPerMicron,
        Shapes = viaInPad ? ViaInPadShapes() : Shapes(),
        Pads = Pads(viaInPad ? Mid : Top),
    });

    private static RailDocument Doc(params RailSpec[] rails)
    {
        var doc = new RailDocument { Name = "pad land" };
        doc.Rails.AddRange(rails);
        return doc;
    }

    /// <summary>
    /// <b>Claim 1.</b> FB1's pads are on TOP, and the MID stub under them is not their copper: the
    /// partition cuts the rail at FB1 rather than reading it BRIDGED, and the DC answer carries the
    /// whole load current through FB1's DCR rather than shorting around it through the stub.
    /// </summary>
    [Fact]
    public void ASeriesPadOverTheRailsOwnFarLayerCopper_IsOnItsOwnLandOnly()
    {
        var run = Run(Doc(Rail()));
        Assert.Null(run.Refusal);

        var result = run.Rails[0];
        foreach (string f in result.Findings) output.WriteLine("finding: " + f);
        Assert.DoesNotContain(result.Findings, f => f.Contains("BRIDGED", StringComparison.Ordinal));

        // The oracle is Ohm's law on the element alone: every amp of the load crosses FB1.
        var ferrite = Assert.Single(result.Breakdown, r => r.Label.Contains("FB1", StringComparison.Ordinal));
        Assert.Equal(DcrOhms * LoadA, ferrite.DropV, 1e-6);
    }

    /// <summary>
    /// <b>Claim 2.</b> The same element on via-in-pad lands the fast model reads as spreading: each
    /// land holds one terminal, so it is an end of the path and not a region the rail is refused for.
    /// </summary>
    [Fact]
    public void ASeriesElementsOwnPadLand_IsATerminalInTheFastModel()
    {
        var run = Run(Doc(Rail()), viaInPad: true);
        output.WriteLine(run.Refusal ?? "solved");
        Assert.Null(run.Refusal);

        var ferrite = Assert.Single(run.Rails[0].Breakdown, r => r.Label.Contains("FB1", StringComparison.Ordinal));
        Assert.Equal(DcrOhms * LoadA, ferrite.DropV, 1e-6);
    }

    /// <summary>
    /// <b>Claim 3.</b> A refused rail refuses itself and the rails it feeds, never an unrelated one;
    /// and a document where nothing solved still refuses whole, with the first rail's sentence.
    /// </summary>
    [Fact]
    public void ARefusedRail_RefusesItselfAndWhatItFeeds_NotAnUnrelatedRail()
    {
        // BROKEN has no FB1 row, so nothing bridges the gap. FED starts at BROKEN's load pad, which
        // is what makes it downstream of BROKEN in RailOrder's chain.
        var run = Run(Doc(
            Rail("BROKEN", withFerrite: false),
            Rail("GOOD"),
            Rail("FED", sourceRefdes: "U1", sourcePin: "VDD", loadRefdes: "U2")));

        Assert.Null(run.Refusal);
        Assert.NotNull(run.Rail("GOOD"));
        Assert.Null(run.RefusalFor("GOOD"));

        Assert.Contains("cannot reach", run.RefusalFor("BROKEN"), StringComparison.Ordinal);
        Assert.Contains("fed from rail 'BROKEN'", run.RefusalFor("FED"), StringComparison.Ordinal);
        Assert.Null(run.Rail("FED"));

        var alone = Run(Doc(Rail("BROKEN", withFerrite: false)));
        Assert.StartsWith("Rail 'BROKEN' was not solved.", alone.Refusal, StringComparison.Ordinal);
        Assert.Equal(alone.Refusal, alone.RefusalFor("BROKEN"));
    }
}
