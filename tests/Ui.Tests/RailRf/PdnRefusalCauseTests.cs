// ================================================================
//  PdnRefusalCauseTests.cs — brief-railrf-29 §3
//
//  The fast model's refusal has to name the CAUSE: two islands nothing joins is a connectivity
//  fact, answered before anything is priced; only a rail that IS joined once its spreading copper
//  is counted is refused for spreading, and then the region named is one on the path — never the
//  largest pour on the board and never a terminal's own landing piece.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnRefusalCauseTests
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    private static Technology Tech()
    {
        var tech = new Technology { Name = "three" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "MID", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Mid] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "MID",
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    private static ViaShape Via(double x, double y) => new()
    {
        Layer = ViaLayer, LandingLayer = Top, X = Mm(x), Y = Mm(y), PadSize = Mm(0.5), DrillSize = Mm(0.3),
    };

    private static PlacedPin Pad(string refdes, string pin, double x, double y) =>
        new(refdes, pin, "VDD", Mm(x), Mm(y), PinSource.BoardNetlist);

    private static PdnExtractionRequest Request(
        List<LayoutShape> shapes, List<PlacedPin> pads,
        IReadOnlyList<PdnSeriesElement>? series = null,
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass>? overrides = null,
        RunControl? control = null)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = 0.03,
        });

        return new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = Tech(),
            DbuPerMicron = DbuPerMicron,
            Pads = pads,
            SeriesElements = series ?? [],
            ClassOverrides = overrides ?? new Dictionary<PdnRegionRef, PdnCopperClass>(),
            Control = control,
        };
    }

    // ── fixture 1: two islands, and a jumper whose pads sit on both ────────────────────────────

    /// <summary>Two 0.3 mm traces with a 1 mm gap between them; JP1's pads straddle the gap. Every
    /// piece of rail copper is trace-shaped, so nothing here could be blamed on spreading.</summary>
    private static PdnExtractionRequest TwoIslands(
        IReadOnlyList<PdnSeriesElement>? series = null, RunControl? control = null) => Request(
        [
            Rect(Top, 0, 0, 20, 0.3),
            Rect(Top, 21, 0, 41, 0.3),
            Rect(Bot, -1, -1, 42, 1.3),
        ],
        [
            Pad("BT1", "1", 0.1, 0.15),
            Pad("JP1", "1", 19.9, 0.15),
            Pad("JP1", "2", 21.1, 0.15),
            Pad("U1", "VDD", 40.9, 0.15),
        ],
        series, control: control);

    private static PdnExtraction Extract(PdnModelKind model, PdnExtractionRequest request) =>
        model == PdnModelKind.Fast ? PdnGraphExtractor.Extract(request) : PdnMeshExtractor.Extract(request);

    /// <summary>Both models: Accurate once skipped this check, meshed the whole board, and was
    /// refused by the netlist backstop in a sentence that blamed the reference layer and named no
    /// part.</summary>
    [Theory]
    [InlineData(PdnModelKind.Fast)]
    [InlineData(PdnModelKind.Accurate)]
    public void TwoIslandsAreRefusedAsSeparateCopperNamingThePartThatBridgesThem(PdnModelKind model)
    {
        var refused = Extract(model, TwoIslands());

        Assert.NotNull(refused.Refusal);
        Assert.Contains("separate copper", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("2 galvanically separate regions", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("JP1", refused.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("spreading", refused.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And its notes say the declared part bridges the regions, not that nothing does.</summary>
    [Theory]
    [InlineData(PdnModelKind.Fast)]
    [InlineData(PdnModelKind.Accurate)]
    public void TheSameRailWithTheBridgeDeclaredAsItsSeriesPartSolves(PdnModelKind model)
    {
        var jp1 = new PdnSeriesElement(
            "JP1", new RailPortAnchor { Refdes = "JP1", Pin = "1" },
            new RailPortAnchor { Refdes = "JP1", Pin = "2" }, 0.005, "test");

        var result = Extract(model, TwoIslands([jp1]));

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Netlist);
        Assert.Contains(result.Diagnostics, d => d.Contains("— JP1 — is what bridges them", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("nothing bridges them", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-rail29-2: a disconnected rail refuses BEFORE any piece is classified or measured — asserted
    /// on the stages the run entered and on the classification it came back with, never on a clock.
    /// </summary>
    [Fact]
    public void TheConnectivityRefusalIsReachedWithoutClassifyingOrPricingAnything()
    {
        var stages = new List<string>();
        var control = new RunControl { Progress = new Recorder(stages), MinReportIntervalMs = 0 };

        var refused = PdnGraphExtractor.Extract(TwoIslands(control: control));

        Assert.NotNull(refused.Refusal);
        Assert.Empty(refused.Classification);
        Assert.Equal(0, control.Completed);
        Assert.DoesNotContain(stages, s => s.Contains("sorting", StringComparison.Ordinal));
        Assert.DoesNotContain(stages, s => s.Contains("measuring", StringComparison.Ordinal));
    }

    /// <summary>
    /// The second connectivity question: one island, but the two top traces meet only through a
    /// stub of the rail's own copper on its REFERENCE layer, which both models set aside — so the
    /// rail is refused before pricing, naming that layer, rather than after it naming a region.
    /// </summary>
    [Fact]
    public void ARailJoinedOnlyOnItsOwnReferenceLayerIsRefusedNamingThatLayer()
    {
        var request = Request(
            [
                Rect(Top, 0, 0, 20, 0.3),
                Rect(Top, 22, 0, 42, 0.3),
                Rect(Bot, 19.7, 0, 22.3, 0.3),     // the rail, on its own return
                Rect(Bot, -1, 0.8, 43, 5),         // the return plane
                Via(19.9, 0.15), Via(22.1, 0.15),
            ],
            [Pad("BT1", "1", 0.1, 0.15), Pad("U1", "VDD", 41.9, 0.15)]);
        request.Technology.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).SpanToLayer = "BOT";

        var refused = PdnGraphExtractor.Extract(request);

        Assert.NotNull(refused.Refusal);
        Assert.Contains("reference return", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("layer 2/0", refused.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("spreading", refused.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(refused.Classification);
    }

    // ── fixture 2: one island, and the only link across it is a spreading square ───────────────

    /// <summary>
    /// The source sits on a 2 × 2 mm TOP land that reaches the MID trace only through a via; the
    /// first MID trace reaches the second only up through a 3 × 3 mm TOP square and back down; and a
    /// 10 × 10 mm TOP pour hangs off the second trace on one via, a dead end. So the square is the
    /// one spreading region on the path, the pour is the largest spreading region on the rail, and
    /// the land is a cut too — but it is the source's own.
    /// </summary>
    private static PdnExtractionRequest SpreadingLink(
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass>? overrides = null) => Request(
        [
            Rect(Top, 0, 0, 2, 2),          // the source's land
            Rect(Mid, 0.7, 0.85, 20, 1.15), // trace 1
            Rect(Top, 19, -0.5, 22, 2.5),   // the link
            Rect(Mid, 21.5, 0.85, 45, 1.15),// trace 2
            Rect(Top, 30, 0, 40, 10),       // the unrelated pour
            Rect(Bot, -1, -1, 46, 11),
            Via(1, 1), Via(19.8, 1), Via(21.6, 1), Via(35, 1),
        ],
        [
            Pad("BT1", "1", 0.3, 0.3),
            Pad("U1", "VDD", 44.8, 1),
        ],
        overrides: overrides);

    [Fact]
    public void ASpreadingOnlyPathNamesTheRegionOnThePathNotTheLargestPourOrTheSourceLand()
    {
        var request = SpreadingLink();
        var refused = PdnGraphExtractor.Extract(request);

        Assert.NotNull(refused.Refusal);

        PdnClassification OnTop(double x, double y) => Assert.Single(refused.Classification,
            c => c.Region.Layer == Top && c.Bounds.Contains(Mm(x), Mm(y)));

        var link = OnTop(20.5, 1);
        var pour = OnTop(35, 5);
        var land = OnTop(1, 1);
        Assert.All(new[] { link, pour, land }, c => Assert.Equal(PdnCopperClass.Spreading, c.Class));

        var fmt = request.LengthFormat;
        Assert.True(refused.Refusal.Contains(
                        $"only through a {fmt.Length(Mm(3))} × {fmt.Length(Mm(3))} region on " +
                        link.Region.Describe(fmt), StringComparison.Ordinal),
                    refused.Refusal);
        Assert.DoesNotContain(pour.Region.Describe(fmt), refused.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(land.Region.Describe(fmt), refused.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decision R-rail29-1 asked for: a spreading piece that lands exactly one terminal is where
    /// current enters the rail and joins it, priced by the coarse mesh like any spreading piece. With
    /// the link forced to trace, the source's land is the only spreading copper between source and
    /// load — and the rail now answers, where it was refused before on every board.
    /// </summary>
    [Fact]
    public void ATerminalsOwnSpreadingLandIsPricedNotRefused()
    {
        var link = PdnGraphExtractor.Extract(SpreadingLink()).Classification
            .Single(c => c.Region.Layer == Top && c.Bounds.Contains(Mm(20.5), Mm(1)));

        var result = PdnGraphExtractor.Extract(SpreadingLink(
            new Dictionary<PdnRegionRef, PdnCopperClass> { [link.Region] = PdnCopperClass.Trace }));

        Assert.Null(result.Refusal);
        Assert.Contains(result.Classification,
            c => c.Region.Layer == Top && c.Bounds.Contains(Mm(1), Mm(1)) && c.Class == PdnCopperClass.Spreading);
    }

    private sealed class Recorder(List<string> stages) : IProgress<RunProgress>
    {
        public void Report(RunProgress p)
        {
            if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage);
        }
    }
}
