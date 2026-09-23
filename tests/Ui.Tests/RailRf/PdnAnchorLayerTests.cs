// ================================================================
//  PdnAnchorLayerTests.cs — brief-railrf-34
//
//  An anchor seeds the copper it means and nothing under it. A pad seeds its land (§1); a coordinate
//  states its layer, or — standing on more than one net — is refused, naming each candidate (§2).
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

public sealed class PdnAnchorLayerTests
{
    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Mid = new(3, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

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
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    /// <summary>
    /// A VDD trace on TOP from BT1 to the load pad at 19.8 mm, the inner plane the reference, and a
    /// 3v3 pour on BOTTOM under the load end — a second supply, joined to VDD by nothing.
    /// </summary>
    private static PdnExtractionRequest Request(RailPortAnchor load, LayerKey? loadLand = null) => new()
    {
        Rail = Rail(load),
        Shapes = [Rect(Top, 0, 0, 20, 0.5), Rect(Mid, -1, -5, 23, 5), Rect(Bot, 15, -2, 22, 2)],
        Technology = Tech(),
        DbuPerMicron = DbuPerMicron,
        Pads =
        [
            new PlacedPin("BT1", "1", "VDD", Mm(0.2), Mm(0.25), PinSource.Artwork) { Layer = Top },
            new PlacedPin("U1", "VDD", "VDD", Mm(19.8), Mm(0.25), PinSource.Artwork) { Layer = loadLand },
        ],
        NetPoints =
        [
            new("VDD", Mm(0.2), Mm(0.25), Top),
            new("VDD", Mm(19.8), Mm(0.25), Top),
            new("3V3", Mm(21), Mm(-1.5), Bot),
        ],
    };

    private static RailSpec Rail(RailPortAnchor load, string name = "VDD")
    {
        var rail = new RailSpec { Name = name, NetName = "VDD", ReferenceLayer = Mid };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
        rail.Loads.Add(new RailLoad { Anchor = load, DcCurrentA = 0.03 });
        return rail;
    }

    private static readonly RailPortAnchor OnThePad = new() { Point = (Mm(19.8), Mm(0.25)) };

    [Fact]
    public void ACoordinateOverTwoNetsIsRefusedNamingBoth_AndWithItsLayerPricesOnlyThatNet()
    {
        foreach (var refused in new[] { PdnGraphExtractor.Extract(Request(OnThePad)),
                                        PdnMeshExtractor.Extract(Request(OnThePad)) })
        {
            Assert.NotNull(refused.Refusal);
            Assert.Contains("'VDD'", refused.Refusal);
            Assert.Contains("'3V3'", refused.Refusal);
            Assert.Contains("\"Layer\": 1", refused.Refusal);
            var a = Assert.Single(refused.AnchorAmbiguities);
            Assert.False(a.IsSource);
            Assert.Equal([Top, Bot], a.Candidates.Select(c => c.Layer));
        }

        var chosen = PdnGraphExtractor.Extract(Request(OnThePad with { Layer = Top }));
        Assert.Null(chosen.Refusal);
        Assert.All(chosen.Regions!.Power.SelectMany(i => i.Copper), c => Assert.Equal(Top, c.Layer));
    }

    [Fact]
    public void APadAnchorSeedsItsLandOnly_WithNoLayerStated()
    {
        var x = PdnGraphExtractor.Extract(Request(new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, loadLand: Top));

        Assert.Null(x.Refusal);
        Assert.All(x.Regions!.Power.SelectMany(i => i.Copper), c => Assert.Equal(Top, c.Layer));
    }

    [Fact]
    public void TheCrailCarriesACoordinatesLayer_AndADocumentWithoutOneReadsAsBefore()
    {
        var doc = new RailDocument();
        doc.Rails.Add(Rail(OnThePad with { Layer = new LayerKey(2, 5) }));
        doc.Rails.Add(Rail(OnThePad, "other"));

        string json = RailDocumentIo.Serialize(doc);
        var back = RailDocumentIo.Deserialize(json);

        Assert.Equal(new LayerKey(2, 5), back.Rails[0].Loads[0].Anchor.Layer);
        Assert.Null(back.Rails[1].Loads[0].Anchor.Layer);
        Assert.Equal(1, json.Split("\"Layer\"").Length - 1);   // nothing written where nothing is stated
    }
}
