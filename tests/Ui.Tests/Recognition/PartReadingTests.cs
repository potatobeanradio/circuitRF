// brief-artsch-4-parts-and-parts-table.md §4 — PartReading on synthetic boards: a placed instance wins
// over the land pattern under it; the bill of materials gives values, refuses a wrong dimension and marks
// a DNP part open; series, shunt and bridged are measured.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartReadingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as4-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void APlacedInstanceWinsOverALandPatternOnTheSamePads()
    {
        // The land drawn at the root stands for the flattened artwork; the instance places a cell whose
        // two pins sit on the same lands.
        var land = Land("0603", DensityLevel.Nominal, 10_000, 5_000);
        var (l, r) = Ends(land);
        string landDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Land"), ViewType.Layout);
        Directory.CreateDirectory(landDir);
        var cell = new LayoutView();
        cell.Pins.Add(new LayoutPin { Name = "1", X = Um(l - 10_000), Y = 0, Layer = Top });
        cell.Pins.Add(new LayoutPin { Name = "2", X = Um(r - 10_000), Y = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(landDir, "Land.clay"), cell);

        var view = PlaneAndLine(0, l);
        view.Shapes.Add(Line(Top, r, 40_000, 5_000));
        view.Shapes.AddRange(land);
        view.Instances.Add(new LayoutInstance { CellRef = Path.Combine("..", "..", "Land"), X = Um(10_000), Y = Um(5_000), RefDes = "C6" });
        string boardDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Board"), ViewType.Layout);
        Directory.CreateDirectory(boardDir);
        string clay = Path.Combine(boardDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var result = ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, ClayPath = clay,
        });

        Assert.True(result.Ok, result.Refusal);
        var row = Assert.Single(result.Parts.Rows);
        Assert.Equal(("C6", PartEvidenceSource.Instance, PartKind.C), (row.Refdes, row.Evidence[PartField.Refdes], row.Kind));
        Assert.Equal((PartConnection.Series, PartConfidence.High), (row.Connection, row.Confidence));
    }

    [Fact]
    public void TheBillOfMaterialsGivesValuesRefusesAWrongDimensionAndMarksDnpOpen()
    {
        var (view, placement, bom) = PartsBoard();

        var result = RecognizeParts(view, placement, bom);

        Assert.True(result.Ok, result.Refusal);
        var r1 = result.Parts.Row("R1")!;
        Assert.Equal((PartKind.R, PartEvidenceSource.Placement, PartEvidenceSource.Bom),
                     (r1.Kind, r1.Evidence[PartField.Refdes], r1.Evidence[PartField.Value]));
        Assert.Equal(4.7, r1.Value!.Value, 12);

        var c2 = result.Parts.Row("C2")!;
        Assert.Null(c2.Value);
        Assert.Equal("C2_C", c2.Variable);
        Assert.Contains(c2.Notes, n => n.Contains("inductance", StringComparison.Ordinal));
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.PartValueWrongDimension));

        Assert.Equal(PartKind.Open, result.Parts.Row("C3")!.Kind);
    }

    [Fact]
    public void SeriesShuntAndBridgedAreMeasuredOffTheIslands()
    {
        var (view, _, _) = PartsBoard();

        var parts = RecognizeParts(view, null, null).Parts;

        // With no placement file every part is a land pattern alone, named top to bottom, left to right.
        Assert.All(parts.Rows, r => Assert.Equal(PartEvidenceSource.Generated, r.Evidence[PartField.Refdes]));
        var byX = parts.Rows.OrderBy(r => r.X).ToList();
        Assert.Equal([PartConnection.Series, PartConnection.Shunt, PartConnection.Bridged], byX.Select(r => r.Connection));
        Assert.All(byX, r => Assert.Equal("0603", r.Case!.Code));
        Assert.False(byX[2].IsModelled);
        Assert.True(byX[1].Terminals[1].OnGround);
    }

    /// <summary>
    /// R-as4-1 (3½): with no land and no mask, a body outline on the silkscreen over a gap is the part — a series one across a
    /// gap cut in a line, a shunt one from the line's edge to the pour — named by the label beside it. Drawn as a polygon, as
    /// four lines and as one closed stroke, they read alike; an outline over unbroken copper, and one round a land pattern's
    /// pads, make no part.
    /// </summary>
    [Fact]
    public void ABodyOutlineOverAGap_IsAPart_NamedByItsLabel_AndOneOverUnbrokenCopperIsNot()
    {
        var silk = new LayerKey(6, 0);
        var tech = TwoLayerWithMask();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = silk, Name = "Silk Top", Purpose = "silkscreen" }];
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 20_000));
        view.Shapes.Add(Line(Top, 0, 10_000, 5_000));                    // a gap from x = 10 to 10.5 mm
        view.Shapes.Add(Line(Top, 10_500, 40_000, 5_000));
        view.Shapes.Add(Rect(Top, 0, 6_000, 40_000, 20_000));            // the pour, 0.525 mm above the line
        for (int i = 0; i < 8; i++) view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 15_000));
        PolygonShape Box(double x0, double y0, double x1, double y1) => new()
            { Layer = silk, Xy = [Um(x0), Um(y0), Um(x1), Um(y0), Um(x1), Um(y1), Um(x0), Um(y1)] };
        LabelShape Label(string text, double x, double y) => new() { Layer = silk, Text = text, X = Um(x), Y = Um(y), Height = Um(800) };

        view.Shapes.Add(Box(9_450, 4_600, 11_050, 5_400));               // C1 across the gap
        view.Shapes.Add(Label("C1", 9_500, 3_200));
        foreach (var (x0, y0, x1, y1) in new[] { (19_600, 4_950, 20_400, 4_950), (20_400, 4_950, 20_400, 6_550),
                                                 (20_400, 6_550, 19_600, 6_550), (19_600, 6_550, 19_600, 4_950) })
            view.Shapes.Add(new PathShape { Layer = silk, Xy = [Um(x0), Um(y0), Um(x1), Um(y1)], Width = 0 });   // L1, as four lines
        view.Shapes.Add(Label("L1", 21_000, 5_500));
        view.Shapes.Add(new PathShape { Layer = silk, Width = Um(150),                                        // R1, one closed stroke
                                        Xy = [Um(29_600), Um(4_950), Um(30_400), Um(4_950), Um(30_400), Um(6_550), Um(29_600), Um(6_550), Um(29_600), Um(4_950)] });
        view.Shapes.Add(Label("R1", 31_000, 5_500));
        view.Shapes.Add(Box(34_200, 4_600, 35_800, 5_400));             // over unbroken line: no part
        view.Shapes.Add(Label("C9", 34_500, 3_200));

        var result = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = tech, Shapes = view.Shapes });

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(["C1", "L1", "R1"], result.Parts.Rows.Select(r => r.Refdes).Order());
        Assert.Equal(PartConnection.Series, result.Parts.Row("C1")!.Connection);
        Assert.Equal((PartConnection.Shunt, PartKind.L), (result.Parts.Row("L1")!.Connection, result.Parts.Row("L1")!.Kind));
        Assert.Equal((PartConnection.Shunt, PartKind.R), (result.Parts.Row("R1")!.Connection, result.Parts.Row("R1")!.Kind));
        Assert.All(result.Parts.Rows, r => Assert.Equal((PartEvidenceSource.Silkscreen, PartConfidence.Medium),
                                                        (r.Evidence[PartField.Refdes], r.Confidence)));
        Assert.Equal(3, result.Report.Count(RecognitionFindingClass.PartsFromBodyOutlines));
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.SilkscreenRefdesNotAssociated));   // C9 names nothing
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.BodyOutlinesOverOnePiece));        // and is said

        // Open strokes with a width are a legend's letters, and never chain into a body; nor is an outline a part when one
        // end has hardly any copper under it.
        var letters = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = tech, Shapes = [.. view.Shapes
            .Where(s => s.Layer != silk)
            .Concat(new[] { (9_450, 4_600, 11_050, 4_600), (11_050, 4_600, 11_050, 5_400), (11_050, 5_400, 9_450, 5_400), (9_450, 5_400, 9_450, 4_600) }
                .Select(e => (LayoutShape)new PathShape { Layer = silk, Xy = [Um(e.Item1), Um(e.Item2), Um(e.Item3), Um(e.Item4)], Width = Um(150) }))
            .Append(Box(29_600, 5_350, 30_400, 6_950))] });
        Assert.Equal(0, letters.Report.Count(RecognitionFindingClass.PartsFromBodyOutlines));

        // A loop chained from lines is a body only as a rectangle: a 0 of a legend exploded into lines — a box with its corners
        // cut, 98 % of its box — makes none. And an outline over three pieces of copper — a three-terminal part's — is said, not
        // read as two terminals.
        (int, int)[] zero = [(9_550, 4_600), (10_950, 4_600), (11_050, 4_700), (11_050, 5_300), (10_950, 5_400), (9_550, 5_400), (9_450, 5_300), (9_450, 4_700)];
        var exploded = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = tech, Shapes = [.. view.Shapes
            .Where(s => s.Layer != silk)
            .Concat(zero.Select((p, i) => (p.Item1, p.Item2, zero[(i + 1) % zero.Length].Item1, zero[(i + 1) % zero.Length].Item2))
                .Select(e => (LayoutShape)new PathShape { Layer = silk, Xy = [Um(e.Item1), Um(e.Item2), Um(e.Item3), Um(e.Item4)], Width = 0 }))
            .Append(Box(9_400, 4_600, 11_100, 6_800))] });   // over both lines either side of the gap, and the pour
        Assert.Equal(0, exploded.Report.Count(RecognitionFindingClass.PartsFromBodyOutlines));
        Assert.Equal(1, exploded.Report.Count(RecognitionFindingClass.BodyOutlinesOverManyPieces));

        // Round a land pattern's pads, an outline is that part's, not a second one.
        var (board, _, _) = PartsBoard();
        board.Shapes.Add(Box(10_500, 4_200, 13_500, 5_800));
        var lands = ArtworkRecognition.Recognize(new RecognitionInput { View = board, Technology = tech, Shapes = board.Shapes });
        Assert.Equal(3, lands.Parts.Rows.Count);
        Assert.Equal(0, lands.Report.Count(RecognitionFindingClass.PartsFromBodyOutlines));
    }

    /// <summary>An 0603 body across a gap in a 0.5 mm coplanar line, wider than the line, with the pours 0.1 mm from it: the
    /// ground under its long edges is a sliver, no third terminal, and the body is the series part across the gap.</summary>
    [Fact]
    public void ABodyWiderThanItsCoplanarLine_IsStillASeriesPart()
    {
        var silk = new LayerKey(6, 0);
        var tech = TwoLayerWithMask();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = silk, Name = "Silk Top", Purpose = "silkscreen" }];
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 4_750, 10_000, 5_250));
        view.Shapes.Add(Rect(Top, 10_500, 4_750, 40_000, 5_250));
        view.Shapes.Add(Rect(Top, 0, 5_350, 40_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 0, 40_000, 4_650));
        for (int i = 0; i < 8; i++) { view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 15_000)); view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 2_000)); }
        view.Shapes.Add(new PolygonShape { Layer = silk, Xy = [Um(9_450), Um(4_600), Um(11_050), Um(4_600), Um(11_050), Um(5_400), Um(9_450), Um(5_400)] });
        view.Shapes.Add(new LabelShape { Layer = silk, Text = "C1", X = Um(9_500), Y = Um(6_000), Height = Um(800) });

        var result = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = tech, Shapes = view.Shapes });

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(PartConnection.Series, Assert.Single(result.Parts.Rows, r => r.Refdes == "C1").Connection);
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.BodyOutlinesOverManyPieces));
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.OpenEnds));
    }

    /// <summary>
    /// Copper under the middle of the 0603 across the coplanar gap: a 0.3 mm island in the gap lies wholly under it, a third
    /// pad whatever its size, and the body is said, not read as two terminals; the top pour's 0.3 mm finger reaching down into
    /// the gap covers what an end must and is one too; its 0.2 mm finger does not, and the body is still the series part.
    /// </summary>
    [Theory]
    [InlineData(10_100.0, 4_850.0, 10_400.0, 5_150.0, false)]
    [InlineData(10_100.0, 4_850.0, 10_400.0, 5_350.0, false)]
    [InlineData(10_150.0, 4_850.0, 10_350.0, 5_350.0, true)]
    public void CopperUnderABodysMiddle_IsAThirdTerminal_AnIslandWhateverItsSize(double x0, double y0, double x1, double y1, bool series)
    {
        var result = CoplanarGap(9_450, 4_600, 11_050, 5_400, Rect(Top, x0, y0, x1, y1));

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(series, result.Parts.Rows.Any(r => r.Refdes == "C1" && r.Connection == PartConnection.Series));
        Assert.Equal(series ? 0 : 1, result.Report.Count(RecognitionFindingClass.BodyOutlinesOverManyPieces));
    }

    /// <summary>An 0805 and a 1206 body across the same gap: the 1206's halves each have more of a pour under them than of the
    /// line, the same pour for both, and the body was skipped with no word. A piece the most under both halves is beside the
    /// part; set aside, with the other pour, the halves take the line either side of the gap, and both are the series part.</summary>
    [Theory]
    [InlineData(9_250.0, 4_375.0, 11_250.0, 5_625.0)]
    [InlineData(8_650.0, 4_200.0, 11_850.0, 5_800.0)]
    public void ABodyWiderThanItsCoplanarLineAndGaps_IsStillASeriesPart(double x0, double y0, double x1, double y1)
    {
        var result = CoplanarGap(x0, y0, x1, y1);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(PartConnection.Series, Assert.Single(result.Parts.Rows, r => r.Refdes == "C1").Connection);
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.BodyOutlinesOverManyPieces));
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.BodyOutlinesOverOnePiece));
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.OpenEnds));
    }

    /// <summary>The board of <see cref="ABodyWiderThanItsCoplanarLine_IsStillASeriesPart"/> — a 0.5 mm coplanar line with a
    /// 0.5 mm gap at x = 10 mm, the pours 0.1 mm from it — with C1's body outline from (<paramref name="x0"/>,
    /// <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>) µm and <paramref name="more"/> copper, read.</summary>
    private static RecognitionResult CoplanarGap(double x0, double y0, double x1, double y1, params LayoutShape[] more)
    {
        var silk = new LayerKey(6, 0);
        var tech = TwoLayerWithMask();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = silk, Name = "Silk Top", Purpose = "silkscreen" }];
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 4_750, 10_000, 5_250));
        view.Shapes.Add(Rect(Top, 10_500, 4_750, 40_000, 5_250));
        view.Shapes.Add(Rect(Top, 0, 5_350, 40_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 0, 40_000, 4_650));
        for (int i = 0; i < 8; i++) { view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 15_000)); view.Shapes.Add(ViaAt(2_500 + 5_000 * i, 2_000)); }
        view.Shapes.Add(new PolygonShape { Layer = silk, Xy = [Um(x0), Um(y0), Um(x1), Um(y0), Um(x1), Um(y1), Um(x0), Um(y1)] });
        view.Shapes.Add(new LabelShape { Layer = silk, Text = "C1", X = Um(x0), Y = Um(y1 + 600), Height = Um(800) });
        view.Shapes.AddRange(more);
        return ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = tech, Shapes = view.Shapes });
    }
}
