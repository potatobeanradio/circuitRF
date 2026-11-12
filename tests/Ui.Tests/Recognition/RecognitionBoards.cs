// Synthetic boards for brief-artsch-3-board-graph.md's gates, built in memory: a two-layer and a
// four-layer technology, and the handful of boards the claims are about. Coordinates in µm.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class RecognitionBoards
{
    public static readonly LayerKey Top = new(1, 0), Bottom = new(2, 0), Plane = new(3, 0), Inner = new(4, 0), Via = new(9, 0),
                                    Mask = new(5, 0);

    public static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static StackupLayer Cu(string name, LayerKey key) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key],
    };

    private static StackupLayer Core(string name, double um) =>
        new() { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Um(um), Epsr = 4.3 };

    /// <summary>Top and Bottom on 500 µm, Bottom the ground reference, a through via.</summary>
    public static Technology TwoLayer()
    {
        var tech = new Technology
        {
            Name = "two-layer",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = Bottom, Name = "Bottom", Purpose = "conductor" },
                new LayerDef { Key = Via, Name = "Drill", Purpose = "via" },
            ],
        };
        tech.Stackup.Layers =
        [
            Cu("Top", Top),
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via], SpanFromLayer = "Top", SpanToLayer = "Bottom" },
            Core("Core", 500),
            Cu("Bottom", Bottom),
        ];
        tech.Stackup.Layers[3].IsGroundReference = true;
        return tech;
    }

    /// <summary><see cref="TwoLayer"/> with a top solder mask, so land patterns read from its openings.</summary>
    public static Technology TwoLayerWithMask()
    {
        var tech = TwoLayer();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = Mask, Name = "Soldermask Top", Purpose = "soldermask" }];
        return tech;
    }

    /// <summary>
    /// A case's land pattern at a density as the generator draws it — two copper lands on Top and two
    /// mask openings — centred at (<paramref name="cxUm"/>, <paramref name="cyUm"/>), along x or along y.
    /// </summary>
    public static List<RectShape> Land(string code, DensityLevel density, double cxUm, double cyUm,
                                       bool vertical = false, double scale = 1)
    {
        // The generator draws on a front copper that sits on the laminate; the boards' via row sits between.
        var tech = TwoLayerWithMask();
        tech.Stackup.Layers = [.. tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via)];
        var result = ChipLandPatternGenerator.Generate(
            FootprintRef.For(SmtCaseTable.Find(code)!, density), tech, PCellLayerSelection.Default);
        long cx = Um(cxUm), cy = Um(cyUm);
        return [.. result.Shapes.OfType<RectShape>().Where(r => r.Layer == Top || r.Layer == Mask).Select(r =>
        {
            long x1 = (long)(r.X1 * scale), y1 = (long)(r.Y1 * scale), x2 = (long)(r.X2 * scale), y2 = (long)(r.Y2 * scale);
            return vertical
                ? new RectShape { Layer = r.Layer, X1 = cx + y1, Y1 = cy + x1, X2 = cx + y2, Y2 = cy + x2 }
                : new RectShape { Layer = r.Layer, X1 = cx + x1, Y1 = cy + y1, X2 = cx + x2, Y2 = cy + y2 };
        })];
    }

    /// <summary>Top, an inner ground plane (the reference), an inner signal layer and Bottom.</summary>
    public static Technology FourLayer()
    {
        var tech = new Technology
        {
            Name = "four-layer",
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top", Purpose = "conductor" },
                new LayerDef { Key = Plane, Name = "Plane", Purpose = "conductor" },
                new LayerDef { Key = Inner, Name = "Inner", Purpose = "conductor" },
                new LayerDef { Key = Bottom, Name = "Bottom", Purpose = "conductor" },
                new LayerDef { Key = Via, Name = "Drill", Purpose = "via" },
            ],
        };
        tech.Stackup.Layers =
        [
            Cu("Top", Top),
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Via], SpanFromLayer = "Top", SpanToLayer = "Bottom" },
            Core("PP1", 200),
            Cu("Plane", Plane),
            Core("Core", 800),
            Cu("Inner", Inner),
            Core("PP2", 200),
            Cu("Bottom", Bottom),
        ];
        tech.Stackup.Layers[3].IsGroundReference = true;
        return tech;
    }

    public static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2, string? net = null) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2), Net = net };

    public static ViaShape ViaAt(double x, double y, double drill = 300) =>
        new() { Layer = Via, X = Um(x), Y = Um(y), DrillSize = Um(drill), PadSize = Um(2 * drill) };

    /// <summary>A 50 Ω-ish line across the board at <paramref name="y"/>, 950 µm wide.</summary>
    public static RectShape Line(LayerKey layer, double x1, double x2, double y) => Rect(layer, x1, y - 475, x2, y + 475);

    /// <summary>
    /// A 40 × 30 mm board: a Bottom plane, a top pour split by a channel for a line running edge to
    /// edge at y = 15 mm, and 200 vias stitching the two pour halves to the plane.
    /// </summary>
    public static LayoutView Stitched(string? groundNamedAt = null)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 30_000));
        view.Shapes.Add(Line(Top, 0, 40_000, 15_000));
        view.Shapes.Add(Rect(Top, 0, 0, 40_000, 14_025));
        view.Shapes.Add(Rect(Top, 0, 15_975, 40_000, 30_000));
        foreach (double y in new double[] { 2_000, 5_000, 8_000, 11_000, 19_000, 22_000, 25_000, 28_000 })
            for (int i = 0; i < 25; i++)
                view.Shapes.Add(ViaAt(1_000 + 1_500 * i, y));
        return view;
    }

    /// <summary>
    /// A 20 × 16 mm board: a Bottom plane, a line edge to edge at y = 5 mm, and a 1.5 × 1 mm shunt pad
    /// beside it carrying six vias to the plane — on its own island, or inside a top pour.
    /// </summary>
    public static LayoutView Shunt(bool padOnPour)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 20_000, 16_000));
        view.Shapes.Add(Line(Top, 0, 20_000, 5_000));
        view.Shapes.Add(Rect(Top, 9_000, 6_000, 10_500, 7_000));
        if (padOnPour) view.Shapes.Add(Rect(Top, 2_000, 6_000, 18_000, 15_000));
        foreach (double y in new double[] { 6_250, 6_750 })
            foreach (double x in new double[] { 9_250, 9_750, 10_250 })
                view.Shapes.Add(ViaAt(x, y, drill: 200));
        return view;
    }

    /// <summary>A Bottom plane 40 × 10 mm and a line on Top from <paramref name="x1"/> to <paramref name="x2"/> at y = 5 mm.</summary>
    public static LayoutView PlaneAndLine(double x1 = 0, double x2 = 40_000)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 10_000));
        view.Shapes.Add(Line(Top, x1, x2, 5_000));
        return view;
    }

    /// <summary>
    /// A 40 × 20 mm board for the parts gates, every land an 0603 drawn with its mask openings:
    /// <list type="bullet">
    /// <item>R1, series, between a line from the left edge and a stub;</item>
    /// <item>C2, shunt, standing on the stub with its far pad strapped to the top ground pour;</item>
    /// <item>C3, bridged, both pads on one line that runs on to the right edge.</item>
    /// </list>
    /// The placement file names all three; the bill of materials gives R1 4R7, C2 10nH and C3 DNP.
    /// </summary>
    public static (LayoutView View, PlacementTable Placement, BomTable Bom) PartsBoard()
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 12_000, 40_000, 20_000));
        for (int i = 0; i < 8; i++) view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 16_000));

        var r1 = Land("0603", DensityLevel.Nominal, 12_000, 5_000);
        var (l1, r) = Ends(r1);
        view.Shapes.AddRange(r1);
        view.Shapes.Add(Rect(Top, 0, 4_525, l1, 5_475));
        view.Shapes.Add(Rect(Top, r, 4_525, 26_000, 5_475));

        double pitch = r - l1;
        var c2 = Land("0603", DensityLevel.Nominal, 20_000, 5_000 + pitch / 2, vertical: true);
        view.Shapes.AddRange(c2);
        view.Shapes.Add(Rect(Top, 19_700, 5_000 + pitch, 20_300, 12_500));

        view.Shapes.AddRange(Land("0603", DensityLevel.Nominal, 33_000, 5_000));
        view.Shapes.Add(Rect(Top, 28_000, 4_525, 40_000, 5_475));

        PlacementRow Row(string refdes, double x, double y) => new(refdes, Um(x), Um(y), 0, false, "0603", 1);
        var placement = new PlacementTable(
            "board.pos", null, PlacementOrigin.BodyCentre, PlacementOriginEvidence.Chosen, LayoutUnit.Mm,
            BoardNetlistUnitsEvidence.Declared, ',',
            [Row("R1", 12_000, 5_000), Row("C2", 20_000, 5_000 + pitch / 2), Row("C3", 33_000, 5_000)],
            3, 0, DrillExtents.Empty, []);
        var bom = new BomTable(
            "board.csv", null, ',',
            [
                new BomRow("R1", "PN-R1", "4R7", "0603", "Resistor"),
                new BomRow("C2", null, "10nH", "0603", "Capacitor"),
                new BomRow("C3", null, "DNP", "0603", "Capacitor"),
            ],
            3, 0, [], []);
        return (view, placement, bom);
    }

    /// <summary>The two land centres along x of a horizontal land, µm.</summary>
    public static (double Left, double Right) Ends(List<RectShape> land)
    {
        var copper = land.Where(s => s.Layer == Top).OrderBy(s => s.X1).ToList();
        return ((copper[0].X1 + copper[0].X2) / 2.0 / LayoutUnits.DefaultDbuPerMicron,
                (copper[1].X1 + copper[1].X2) / 2.0 / LayoutUnits.DefaultDbuPerMicron);
    }

    public static RecognitionResult RecognizeParts(LayoutView view, PlacementTable? placement, BomTable? bom) =>
        CopperAgrees(ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, Placement = placement, Bom = bom,
        }));

    public static RecognitionResult Recognize(
        LayoutView view, Technology tech, RecognitionOptions? options = null, RecognitionScope? scope = null,
        CircuitRF.Design.Layout.Em.EmSetup? setup = null) =>
        CopperAgrees(ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = tech, Shapes = view.Shapes, EmSetup = setup,
            Options = options ?? new RecognitionOptions(), Scope = scope ?? RecognitionScope.Whole,
        }));

    /// <summary>The recognition's check of its circuit against its copper is an error in a test: nothing split, nothing joined
    /// across islands, nothing joined across copper no line models (docs/design/artwork-to-schematic.md §6.7½).</summary>
    public static RecognitionResult CopperAgrees(RecognitionResult result, string? what = null)
    {
        foreach (var cls in new[] { RecognitionFindingClass.CircuitSplitsCopper, RecognitionFindingClass.CircuitJoinsAcrossCopper,
                                    RecognitionFindingClass.CopperJoinedWithoutLine })
            Assert.True(result.Report.Count(cls) == 0, $"{what}{(what is null ? "" : ": ")}{result.Report.Of(cls)?.Sentence}");
        return result;
    }
}
