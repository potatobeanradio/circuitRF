// ================================================================
//  PdnReturnNetTests.cs — brief-railrf-31
//
//  The return is a NET, not a layer. A rail seed never claims it (§2); the reference is that net's
//  copper and a return that cannot be resolved is refused where the rail is on its layer (§3); and a
//  split return is never solved — refused by the galvanic check up front, and by the netlist
//  backstop in PdnAssembly when only the READING splits it (§4).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnReturnNetTests
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>Three conductors, the inner one the reference, and a through via.</summary>
    private static Technology Tech()
    {
        var tech = new Technology { Name = "three" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Mm(0.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "MID", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Mid], IsGroundReference = true },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot] },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
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

    /// <summary>A 0.5 mm VDD trace on TOP from BT1.1 to U1.VDD, over MID.</summary>
    private static PdnExtractionRequest Request(List<LayoutShape> shapes, IReadOnlyList<PdnNetPoint> netPoints)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Mid };
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
            Shapes = [Rect(Top, 0, 0, 20, 0.5), .. shapes],
            Technology = Tech(),
            DbuPerMicron = DbuPerMicron,
            Pads =
            [
                new PlacedPin("BT1", "1", "VDD", Mm(0.2), Mm(0.25), PinSource.BoardNetlist),
                new PlacedPin("U1", "VDD", "VDD", Mm(19.8), Mm(0.25), PinSource.BoardNetlist),
            ],
            NetPoints = netPoints,
        };
    }

    // ── §2: the load pad over a ground pour on the far side ──────────────────────────────────────

    /// <summary>The inner GND plane, the BOTTOM GND pour under the load pad, and the two vias that
    /// stitch them — so the pour is galvanically the reference plane, as on the reported board.</summary>
    private static List<LayoutShape> PlaneAndPour(bool pour) =>
    [
        Rect(Mid, -1, -5, 21, 5),
        .. pour ? new LayoutShape[] { Rect(Bot, 15, -2, 22, 2), Via(16, -1.5), Via(21, 1.5) }
                : [Via(16, -1.5), Via(21, 1.5)],
    ];

    private static readonly PdnNetPoint[] Named =
    [
        new("VDD", Mm(0.2), Mm(0.25)),
        new("VDD", Mm(19.8), Mm(0.25)),
        new("GND", Mm(16), Mm(-1.5)),
        new("GND", Mm(21), Mm(1.5)),
    ];

    [Fact]
    public void ARailSeedOverAGroundPourDoesNotClaimIt_AndTheAnswerIsTheBoardWithoutThePour()
    {
        var withPour = PdnGraphExtractor.Extract(Request(PlaneAndPour(pour: true), Named));
        var without = PdnGraphExtractor.Extract(Request(PlaneAndPour(pour: false), Named));

        Assert.Null(withPour.Refusal);
        Assert.Null(without.Refusal);

        // The rail is the TOP trace and nothing else — no pour, no plane.
        Assert.All(withPour.Regions!.Power.SelectMany(i => i.Copper), c => Assert.Equal(Top, c.Layer));
        Assert.Equal(PdnReturnNetBasis.Measured, withPour.Netlist!.Provenance.ReturnNet.Basis);
        Assert.Equal("GND", withPour.Netlist.Provenance.ReturnNet.Net);

        // Neither rail nor reference, the pour contributes nothing: IDENTICAL, not close.
        static string Dump(PdnExtraction x) => string.Join("\n", x.Netlist!.Netlist.Components.Select(c =>
            $"{c.ComponentType} {c.InstancePath} [{string.Join(",", c.Nodes)}] " +
            string.Join(" ", c.Parameters.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"))));
        Assert.Equal(Dump(without), Dump(withPour));
    }

    /// <summary>
    /// A return point claims only the copper it stands on. A GND pad on TOP (its land stated, as an
    /// artwork pad's is) over the rail's own VDD copper on the reference layer: reading "the
    /// reference-layer piece under every GND point" took the VDD net as the return and then removed
    /// it from its own rail, which came back as copper of no net at all.
    /// </summary>
    [Fact]
    public void AGroundPadOverTheRailsCopperOnTheReferenceLayerDoesNotTakeTheRailAsTheReturn()
    {
        var result = PdnGraphExtractor.Extract(Request(
        [
            Rect(Mid, -1, -5, 21, -1),                  // the GND plane
            Via(10, -3),                                 // and a GND via onto it
            Rect(Mid, 5, 0, 15, 3), Via(6, 0.25),        // VDD copper on the reference layer, joined up
            Rect(Top, 9, 2, 11, 2.8),                    // a GND pad over it, on TOP
        ],
        [
            new("VDD", Mm(0.2), Mm(0.25)), new("VDD", Mm(19.8), Mm(0.25)),
            new("GND", Mm(10), Mm(-3)), new("GND", Mm(10), Mm(2.4), Top),
        ]));

        Assert.Null(result.Refusal);
        Assert.Equal("GND", result.Netlist!.Provenance.ReturnNet.Net);
        Assert.Contains(result.Regions!.Power.SelectMany(i => i.Copper), c => c.Layer == Top);
    }

    // ── §3: no net to say which copper is the return, and the rail on the reference layer ───────

    [Fact]
    public void AnUnresolvableReturnWithTheRailOnItsLayerIsRefusedNamingTheLayerAndTheNet()
    {
        // A VDD via down through an antipad, with a land on MID — the rail's own copper on the
        // reference layer — and no net points at all.
        var refused = PdnGraphExtractor.Extract(Request(
        [
            Rect(Mid, -1, -5, 9.4, 5), Rect(Mid, 10.6, -5, 21, 5),
            Rect(Mid, 9.4, -5, 10.6, -0.35), Rect(Mid, 9.4, 0.85, 10.6, 5),
            Rect(Mid, 9.7, 0, 10.3, 0.5),
            Via(10, 0.25),
        ], []));

        Assert.NotNull(refused.Refusal);
        Assert.Contains("layer 3/0", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("'VDD'", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("ReferenceNet", refused.Refusal, StringComparison.Ordinal);
    }

    // ── §4: a split return ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AReferenceSplitInTwoIsRefusedByTheGalvanicCheck()
    {
        var refused = PdnGraphExtractor.Extract(Request(
            [Rect(Mid, -1, -5, 9.9, 5), Rect(Mid, 10.1, -5, 21, 5)], []));

        Assert.NotNull(refused.Refusal);
        Assert.Contains("no single return", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("layer 3/0", refused.Refusal, StringComparison.Ordinal);
        Assert.Empty(refused.Classification);
    }

    /// <summary>
    /// The backstop alone: the two plane halves ARE one galvanic piece — joined through a BOTTOM strip
    /// and two vias — so the galvanic check passes, but both models read the reference layer's copper
    /// only and the stamped netlist is two circuits. Refused in PdnAssembly, by both.
    /// </summary>
    [Theory]
    [InlineData(PdnModelKind.Fast)]
    [InlineData(PdnModelKind.Accurate)]
    public void AReturnOnlyTheReadingSplitsIsRefusedByTheNetlistBackstop(PdnModelKind model)
    {
        var request = Request(
        [
            Rect(Mid, -1, -5, 9, 5), Rect(Mid, 11, -5, 21, 5),
            Rect(Bot, 5, -4.5, 15, -3.5), Via(5.5, -4), Via(14.5, -4),
        ], []);

        var result = model == PdnModelKind.Fast
            ? PdnGraphExtractor.Extract(request)
            : PdnMeshExtractor.Extract(request);

        Assert.Single(result.Regions!.Reference);
        Assert.NotNull(result.Refusal);
        Assert.Contains("does not join to the source's return", result.Refusal, StringComparison.Ordinal);
        Assert.Contains(model == PdnModelKind.Fast ? "Fast reading" : "Accurate reading",
                        result.Refusal, StringComparison.Ordinal);
    }
}
