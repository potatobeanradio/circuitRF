// ================================================================
//  DxfBoardImportTests.cs — brief DFR38-1: a board drawn as DXF outlines imports as copper and vias, and
//  a DXF nobody was asked about, or one of our own exports, imports exactly as it did.
//
//  testdata/interchange/dxf-board/outlines.dxf holds, on PATTERN: a pour whose bottom edge is two LINEs
//  overlapping from x = 8 to 12 mm, a closed clearance in it, a line in the clearance, and three sides of
//  a box open across x = 25. HOLE has two CIRCLEs and an INSERT of a block whose CIRCLE is on layer 0.
//  UNUSED is a block model space never inserts, and its only entity is on NOTES.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Tests;

public sealed class DxfBoardImportTests : IDisposable
{
    private const string BoardMap = "PATTERN=Top Copper,HOLE=Drill,PARTS=Silk Top,SHELL=Outline";
    internal const string TechId = "pcb-2layer_RO4350B_20mil_1oz";

    private readonly string _root = Directory.CreateTempSubdirectory("crf-dxf-board-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Outlines() => Path.Combine(RepoRoot(), "testdata", "interchange", "dxf-board", "outlines.dxf");

    private static Technology Tech() => ShippedTechnologies.Load(TechId);

    private static LayerKey LayerNamed(Technology tech, string name) => tech.Layers.First(l => l.Name == name).Key;

    private DxfImport.ImportResult Import(string dxfPath, string dir, Technology tech, string? map, string? importName = null)
    {
        using var stream = File.OpenRead(dxfPath);
        return DxfImport.Import(stream, Path.Combine(_root, dir), tech, LayoutUnits.DefaultDbuPerMicron,
            resolveMapping: map is null ? null : request =>
            {
                var (rows, refusal) = DxfBoardReading.ApplyMap(map, request.Rows, tech);
                Assert.Null(refusal);
                return new DxfMappingAnswer(rows!);
            },
            importName: importName);
    }

    private static LayoutView LoadOnly(DxfImport.ImportResult result)
    {
        string cell = Assert.Single(result.CreatedCellDirs);
        return LayoutPersistence.LoadFromFile(Directory.GetFiles(CellFolder.SubFolderPath(cell, ViewType.Layout), "*.clay").Single());
    }

    /// <summary>Every .clay under a folder, by path relative to it.</summary>
    private static Dictionary<string, byte[]> Clays(string dir) =>
        Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

    [Fact]
    public void Outlines_BecomeOnePolygonWithOneHole_PlusOnePolygon_TheGapReported_CirclesAsVias_NoCellForAnUnusedBlock()
    {
        var tech = Tech();
        var result = Import(Outlines(), "a", tech, BoardMap);

        var view = LoadOnly(result);
        var copper = view.Shapes.Where(s => s.Layer == LayerNamed(tech, "Top Copper")).ToList();

        var polygons = copper.OfType<PolygonShape>().ToList();
        Assert.Equal(2, polygons.Count);
        Assert.Single(polygons, p => p.Holes?.Count == 1);
        Assert.Single(polygons, p => p.Holes is null);

        Assert.Equal(3, copper.OfType<PathShape>().Count());   // the open box's three sides, still outlines
        Assert.Contains(result.Messages, m => m.Contains("(25, 0) and (25, 5)", StringComparison.Ordinal));

        var vias = view.Shapes.OfType<ViaShape>().ToList();
        Assert.Equal(3, vias.Count);
        Assert.All(vias, v => Assert.Equal(LayerNamed(tech, "Drill"), v.Layer));
        Assert.All(vias, v => Assert.Equal(LayerNamed(tech, "Top Copper"), v.LandingLayer));
        Assert.Contains(vias, v => v.X == 2_000_000 && v.Y == 8_000_000);   // the block's, on layer 0, inserted on HOLE

        Assert.Single(view.Shapes.OfType<PolygonShape>(), p => p.Layer == LayerNamed(tech, "Outline"));
        Assert.Contains(result.Messages, m => m.Contains("1 block(s) model space does not reach", StringComparison.Ordinal));
    }

    /// <summary>DXF text of one ENTITIES section, millimetres: <paramref name="entities"/> are group-code, value pairs.</summary>
    private string Drawing(string name, params (int Code, object Value)[] entities)
    {
        var text = new System.Text.StringBuilder("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n");
        foreach (var (code, value) in entities)
            text.Append(code).Append('\n').Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        text.Append("0\nENDSEC\n0\nEOF\n");
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text.ToString());
        return path;
    }

    private static (int, object)[] Polyline(string layer, params (double X, double Y, double Bulge)[] points)
    {
        var groups = new List<(int, object)> { (0, "LWPOLYLINE"), (8, layer), (90, points.Length), (70, 1) };
        foreach (var (x, y, b) in points)
        {
            groups.Add((10, x));
            groups.Add((20, y));
            if (b != 0) groups.Add((42, b));
        }
        return [.. groups];
    }

    private static (int, object)[] Line(string layer, double x0, double y0, double x1, double y1) =>
        [(0, "LINE"), (8, layer), (10, x0), (20, y0), (11, x1), (21, y1)];

    /// <summary>A regular polygon of <paramref name="n"/> corners on HOLE centred at (<paramref name="x"/>, 5) mm, the circle inside
    /// it 0.4 mm across, its first corner at <paramref name="startDeg"/>.</summary>
    private static (int, object)[] Regular(string layer, double x, int n, double startDeg)
    {
        double r = 0.2 / Math.Cos(Math.PI / n);
        return Polyline(layer, [.. Enumerable.Range(0, n).Select(k =>
        {
            double a = (startDeg + 360.0 * k / n) * Math.PI / 180;
            return (x + r * Math.Cos(a), 5 + r * Math.Sin(a), 0.0);
        })]);
    }

    /// <summary>R-dfr38-1-4 as revised: on a drill layer a circle is a via, and so is a closed, convex outline square to its box
    /// and filling it, or a regular polygon, its drill the circle inside it — a square (closed; four lines; its closing vertex
    /// repeated; a corner drawn twice), a 45° diamond and the same of four lines, a rounded square, a triangle, a hexagon, an
    /// octagon, a two-arc polyline. A circle in a square, and two circles, at one centre are one via, the smaller. A long
    /// rectangle, a 1.3:1 one, and a square wider than the layer's widest circle are no holes and are said.</summary>
    [Fact]
    public void RegularPolygonsOnADrillLayer_AreVias_OneSymbolIsOneVia_AndARectangleOrAnOversizeSquareIsLeftAsDrawn()
    {
        const double b = 0.41421356237309503;   // tan(π/8): a quarter-circle corner
        double d = 0.2 * Math.Sqrt(2);
        var entities = new List<(int, object)>();
        entities.AddRange(Polyline("PATTERN", (0, 0, 0), (44, 0, 0), (44, 10, 0), (0, 10, 0)));
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 2.0), (20, 5.0), (40, 0.2)]);
        entities.AddRange(Regular("HOLE", 4, 4, 45));                                // a square
        entities.AddRange(Line("HOLE", 5.8, 4.8, 6.2, 4.8));                         // a square of four lines
        entities.AddRange(Line("HOLE", 6.2, 4.8, 6.2, 5.2));
        entities.AddRange(Line("HOLE", 6.2, 5.2, 5.8, 5.2));
        entities.AddRange(Line("HOLE", 5.8, 5.2, 5.8, 4.8));
        entities.AddRange(Regular("HOLE", 8, 8, 22.5));                              // an octagon
        entities.AddRange(Polyline("HOLE", (9.8, 5, 1), (10.2, 5, 1)));             // two arcs
        entities.AddRange(Regular("HOLE", 12, 6, 0));                                // a hexagon
        entities.AddRange(Regular("HOLE", 14, 4, 0));                                // a diamond
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 16.0), (20, 5.0), (40, 0.2)]);
        entities.AddRange(Regular("HOLE", 16, 4, 45));                               // the same drill, its square drawn round it
        entities.AddRange(Polyline("HOLE", (23.8, 4.8, 0), (24.2, 4.8, 0), (24.2, 5.2, 0), (23.8, 5.2, 0), (23.8, 4.8, 0)));   // closing vertex repeated
        entities.AddRange(Polyline("HOLE", (25.8, 4.8, 0), (26.2, 4.8, 0), (26.2, 4.8, 0), (26.2, 5.2, 0), (25.8, 5.2, 0)));   // a corner twice
        entities.AddRange(Line("HOLE", 28 + d, 5, 28, 5 + d));                       // a diamond of four lines
        entities.AddRange(Line("HOLE", 28, 5 + d, 28 - d, 5));
        entities.AddRange(Line("HOLE", 28 - d, 5, 28, 5 - d));
        entities.AddRange(Line("HOLE", 28, 5 - d, 28 + d, 5));
        entities.AddRange(Regular("HOLE", 30, 3, 90));                               // a triangle, a drill chart's symbol
        entities.AddRange(Polyline("HOLE", (31.88, 4.8, 0), (32.12, 4.8, b), (32.2, 4.88, 0), (32.2, 5.12, b), (32.12, 5.2, 0),
                                           (31.88, 5.2, b), (31.8, 5.12, 0), (31.8, 4.88, b)));                     // a rounded square
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 34.0), (20, 5.0), (40, 0.2)]);
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 34.0), (20, 5.0), (40, 0.4)]);                         // two circles, one centre
        entities.AddRange(Polyline("HOLE", (35.74, 4.8, 0), (36.26, 4.8, 0), (36.26, 5.2, 0), (35.74, 5.2, 0)));   // 1.3:1
        entities.AddRange(Polyline("HOLE", (38, 4.8, 0), (40, 4.8, 0), (40, 5.2, 0), (38, 5.2, 0)));               // a long rectangle
        entities.AddRange(Polyline("HOLE", (41.5, 4.5, 0), (42.5, 4.5, 0), (42.5, 5.5, 0), (41.5, 5.5, 0)));       // 1 mm: past the circles

        var tech = Tech();
        var result = Import(Drawing("drill-marks.dxf", [.. entities]), "marks", tech, "PATTERN=Top Copper,HOLE=Drill");
        var view = LoadOnly(result);

        var vias = view.Shapes.OfType<ViaShape>().OrderBy(v => v.X).ToList();
        Assert.Equal([2_000_000, 4_000_000, 6_000_000, 8_000_000, 10_000_000, 12_000_000, 14_000_000, 16_000_000,
                      24_000_000, 26_000_000, 28_000_000, 30_000_000, 32_000_000, 34_000_000],
                     vias.Select(v => (long)Math.Round(v.X / 1e6) * 1_000_000));
        Assert.All(vias, v => Assert.InRange(v.Y, 4_999_000, 5_001_000));
        Assert.All(vias, v => Assert.InRange(v.DrillSize, 398_000, 401_000));
        Assert.Contains(result.Messages, m => m.Contains("4 squares, 3 circles, 3 polygons, 2 arc outlines, 2 squares of lines read as holes",
                                                         StringComparison.Ordinal));
        Assert.Contains(result.Messages, m => m.Contains("HOLE: 2 marks were drawn at another's centre", StringComparison.Ordinal));

        var drill = LayerNamed(tech, "Drill");
        Assert.Equal(3, view.Shapes.Count(s => s.Layer == drill && s is PolygonShape));   // the two rectangles and the 1 mm square
        Assert.DoesNotContain(view.Shapes, s => s.Layer == drill && s is PathShape);        // every line drew a hole
        Assert.Contains(result.Messages, m => m.Contains("HOLE: 3 entities are not a circle or a small square or regular outline", StringComparison.Ordinal));
    }

    /// <summary>A mark's hole is the circle inside it about its centre, whichever rule takes it: an octagon with a corner on the
    /// axis, or turned 10°, is 0.4 mm across its flats, not the 0.433 or 0.426 mm of its box; a trapezoid of 0.4 and 0.2 mm sides
    /// 0.4 mm high and a square with a corner cut by half its side are square to their boxes and fill them, and hold only
    /// 0.291 and 0.283 mm about their boxes' centres, not 0.4.</summary>
    [Fact]
    public void AMarksHole_IsTheCircleInsideIt()
    {
        var entities = new List<(int, object)>();
        entities.AddRange(Polyline("PATTERN", (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0)));
        entities.AddRange(Regular("HOLE", 2, 8, 0));
        entities.AddRange(Regular("HOLE", 4, 8, 10));
        entities.AddRange(Polyline("HOLE", (5.8, 4.8, 0), (6.2, 4.8, 0), (6.1, 5.2, 0), (5.9, 5.2, 0)));
        entities.AddRange(Polyline("HOLE", (7.8, 4.8, 0), (8.2, 4.8, 0), (8.2, 5.0, 0), (8.0, 5.2, 0), (7.8, 5.2, 0)));

        var result = Import(Drawing("inside.dxf", [.. entities]), "inside", Tech(), "PATTERN=Top Copper,HOLE=Drill");
        var vias = LoadOnly(result).Shapes.OfType<ViaShape>().OrderBy(v => v.X).ToList();

        Assert.Equal(4, vias.Count);
        Assert.InRange(vias[0].DrillSize, 399_000, 401_000);
        Assert.InRange(vias[1].DrillSize, 399_000, 401_000);
        Assert.InRange(vias[2].DrillSize, 290_000, 292_000);   // 2 × 0.06 / √0.17 mm
        Assert.InRange(vias[3].DrillSize, 282_000, 284_000);   // 0.2 √2 mm
        Assert.All(vias, v => Assert.InRange(v.Y, 4_999_000, 5_001_000));
    }

    /// <summary>A pad round a drill is the via's pad whatever its shape — a square pad at a round drill (a via in the pad, a
    /// square first pin), a square pad at a square drill, a round pad at a round one — and a square pad keeps its whole square
    /// as copper: the via's land is the circle inside it.</summary>
    [Fact]
    public void ASquarePadRoundADrill_IsTheViasPad_AndKeepsItsShape()
    {
        var entities = new List<(int, object)>();
        entities.AddRange(Polyline("PATTERN", (3.4, 4.4, 0), (4.6, 4.4, 0), (4.6, 5.6, 0), (3.4, 5.6, 0)));
        entities.AddRange([(0, "CIRCLE"), (8, "PATTERN"), (10, 8.0), (20, 5.0), (40, 0.6)]);
        entities.AddRange(Polyline("PATTERN", (11.4, 4.4, 0), (12.6, 4.4, 0), (12.6, 5.6, 0), (11.4, 5.6, 0)));
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 4.0), (20, 5.0), (40, 0.2)]);
        entities.AddRange([(0, "CIRCLE"), (8, "HOLE"), (10, 8.0), (20, 5.0), (40, 0.2)]);
        entities.AddRange(Regular("HOLE", 12, 4, 45));

        var tech = Tech();
        var result = Import(Drawing("pads.dxf", [.. entities]), "pads", tech, "PATTERN=Top Copper,HOLE=Drill");
        var view = LoadOnly(result);

        var vias = view.Shapes.OfType<ViaShape>().OrderBy(v => v.X).ToList();
        Assert.Equal(3, vias.Count);
        Assert.All(vias, v => Assert.InRange(v.PadSize, 1_198_000, 1_202_000));
        var top = LayerNamed(tech, "Top Copper");
        var squares = view.Shapes.Where(s => s.Layer == top).OfType<PolygonShape>().ToList();
        Assert.Equal(2, squares.Count);
        static double Area(long[] xy) => Math.Abs(Enumerable.Range(0, xy.Length / 2).Sum(i =>
            (double)xy[2 * i] * xy[(2 * i + 3) % xy.Length] - (double)xy[(2 * i + 2) % xy.Length] * xy[2 * i + 1])) / 2;
        Assert.All(squares, sq => Assert.InRange(Area(sq.Xy), 1.43e12, 1.45e12));
        Assert.Empty(view.Shapes.Where(s => s.Layer == top).OfType<CircleShape>());
    }

    [Fact]
    public void AGapWithinTheTolerance_Closes_AndALargerOneDoesNot()
    {
        long[] box = [0, 0, 1_000_000, 0, 1_000_000, 1_000_000, 0, 1_000_000, 0, 500];

        var closed = DxfCopperLoops.Chain([box], [], joinToleranceDbu: 1_000);
        Assert.Single(closed.Loops);
        Assert.Empty(closed.Open);
        Assert.Equal(500, closed.LargestJoinDbu);

        var open = DxfCopperLoops.Chain([box], [], joinToleranceDbu: 100);
        Assert.Empty(open.Loops);
        Assert.Single(open.Open);
    }

    [Fact]
    public void TwoLoopsDrawnSharingAnEdge_AreOnePolygon_AndInvertTakesTheOtherParity()
    {
        var shared = DxfCopperLoops.Chain([], [[0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
                                               [100_000, 0, 200_000, 0, 200_000, 100_000, 100_000, 100_000]], 1_000);
        var merged = Assert.Single(DxfCopperLoops.Fill(shared.Loops, invert: false));
        Assert.Equal(1, shared.SharedEdgesRemoved);
        Assert.Equal(4, merged.Xy.Length / 2);

        var nested = DxfCopperLoops.Chain([], [[0, 0, 300_000, 0, 300_000, 300_000, 0, 300_000],
                                               [100_000, 100_000, 200_000, 100_000, 200_000, 200_000, 100_000, 200_000]], 1_000);
        Assert.NotNull(Assert.Single(DxfCopperLoops.Fill(nested.Loops, invert: false)).Holes);
        Assert.Null(Assert.Single(DxfCopperLoops.Fill(nested.Loops, invert: true)).Holes);
    }

    /// <summary>Left on the dialog's defaults, with nothing read as copper, a board is still one cell and no more.</summary>
    [Fact]
    public void AnAnsweredImport_IsOneCellNamedAfterTheFile_WhateverItsLayersReadAs()
    {
        var result = Import(Outlines(), "w", Tech(), "PATTERN=add,HOLE=add,PARTS=add,SHELL=add", importName: "outlines");

        Assert.Equal("outlines", Path.GetFileName(Assert.Single(result.CreatedCellDirs)));
        Assert.Equal(["outlines"], Directory.GetDirectories(Path.Combine(_root, "w")).Select(Path.GetFileName));
        Assert.Empty(LoadOnly(result).Instances);
    }

    [Fact]
    public void AnImportNobodyWasAskedAbout_IsUnchangedByteForByte()
    {
        var tech = Tech();
        var plain = Import(Outlines(), "plain", tech, null);

        using var stream = File.OpenRead(Outlines());
        var kept = DxfImport.Import(stream, Path.Combine(_root, "kept"), tech, LayoutUnits.DefaultDbuPerMicron,
            resolveMapping: request => new DxfMappingAnswer(
                [.. request.Rows.Select(r => r with { Reading = DxfLayerReading.AsDrawn })], KeepBlocks: true));

        var a = Clays(Path.Combine(_root, "plain"));
        var b = Clays(Path.Combine(_root, "kept"));
        Assert.Contains(a.Keys, k => k.Contains("UNUSED", StringComparison.Ordinal));
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (name, bytes) in a) Assert.Equal(bytes, b[name]);
        Assert.Equal(plain.LayersToAdd.Select(l => l.Key), kept.LayersToAdd.Select(l => l.Key));
    }

    /// <summary>A file that keeps a cell per block lands in one folder, not across the workspace.</summary>
    [Fact]
    public void SeveralCells_GoInOneFolderNamedAfterTheFile()
    {
        var tech = Tech();
        using var stream = File.OpenRead(Outlines());
        var result = DxfImport.Import(stream, Path.Combine(_root, "w"), tech, LayoutUnits.DefaultDbuPerMicron,
            resolveMapping: request => new DxfMappingAnswer(request.Rows, KeepBlocks: true), importName: "outlines");

        Assert.True(result.CreatedCellDirs.Count > 1);
        Assert.Equal(["outlines"], Directory.GetDirectories(Path.Combine(_root, "w")).Select(Path.GetFileName));
    }

    /// <summary>A layout of ours exported and re-imported asks nothing and reads nothing: a pad on a pour is two
    /// closed polylines, which an even-odd fill would punch out of it, and a via is two circles.</summary>
    [Fact]
    public void OurOwnExport_RoundTrips_AsDrawn_AndAsksNothing()
    {
        var tech = Tech();
        var top = LayerNamed(tech, "Top Copper");
        var drill = LayerNamed(tech, "Drill");
        var sub = Path.Combine(_root, "source");
        Directory.CreateDirectory(sub);

        string cell = CellFolder.CreateCellFolder(sub, "Board");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = top, X1 = 0, Y1 = 0, X2 = 10_000_000, Y2 = 10_000_000 });
        view.Shapes.Add(new RectShape { Layer = top, X1 = 2_000_000, Y1 = 2_000_000, X2 = 3_000_000, Y2 = 3_000_000 });
        view.Shapes.Add(new PolygonShape
        {
            Layer = top, Xy = [20_000_000, 0, 30_000_000, 0, 30_000_000, 10_000_000, 20_000_000, 10_000_000],
            Holes = [[22_000_000, 2_000_000, 28_000_000, 2_000_000, 28_000_000, 8_000_000, 22_000_000, 8_000_000]],
        });
        view.Shapes.Add(new PathShape { Layer = top, Xy = [0, 12_000_000, 5_000_000, 12_000_000], Width = 300_000, End = PathEndStyle.Flush });
        view.Shapes.Add(new ViaShape { Layer = drill, LandingLayer = top, X = 5_000_000, Y = 5_000_000, PadSize = 600_000, DrillSize = 300_000 });
        string file = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), "Board.clay");
        LayoutPersistence.SaveToFile(file, view);
        var ccell = CellPersistence.LoadFromFile(Path.Combine(cell, CellFolder.CcellFileName));
        ccell.PrimaryLayout = "Board.clay";
        CellPersistence.SaveToFile(Path.Combine(cell, CellFolder.CcellFileName), ccell);

        string dxf = Path.Combine(_root, "board.dxf");
        DxfExport.Write(dxf, DxfExport.Analyze(cell, tech, 1000), new DxfExportOptions(InsUnits: DxfUnits.Millimeters));

        bool? asked = null;
        using (var stream = File.OpenRead(dxf))
            DxfImport.Import(stream, Path.Combine(_root, "viaRequest"), tech, 1000, resolveMapping: request =>
            {
                asked = request.NeedsAnswer;
                return new DxfMappingAnswer(request.Rows, KeepBlocks: true);
            });
        Assert.False(asked);

        Import(dxf, "plain", tech, null);
        var viaRequest = Clays(Path.Combine(_root, "viaRequest"));
        foreach (var (name, bytes) in Clays(Path.Combine(_root, "plain"))) Assert.Equal(bytes, viaRequest[name]);

        // And what came back is what went out: both pads, and the hatched polygon's hole.
        string back = viaRequest.Keys.First(k => k.EndsWith("Board.clay", StringComparison.Ordinal));
        var read = LayoutPersistence.LoadFromFile(Path.Combine(_root, "viaRequest", back));
        var copper = read.Shapes.Where(s => s.Layer == top).ToList();
        Assert.Equal(2, copper.OfType<PolygonShape>().Count(p => p.Holes is null && p.Xy.Length == 8));
        Assert.Single(copper.OfType<PolygonShape>(), p => p.Holes?.Count == 1);
        Assert.Single(copper.OfType<PathShape>(), p => p.Width == 300_000);
    }

    /// <summary>An imported DXF's cell carries model space's own name, so exporting it writes a block of that name.
    /// Read back, the two shared a cell folder and the thin model space overwrote every shape.</summary>
    [Fact]
    public void AnImportedBoard_ExportedAndImportedAgain_KeepsItsCopper()
    {
        var tech = Tech();
        var top = LayerNamed(tech, "Top Copper");
        var board = Import(Outlines(), "first", tech, BoardMap);
        var before = LoadOnly(board).Shapes.OfType<PolygonShape>().Where(p => p.Layer == top).ToList();

        string dxf = Path.Combine(_root, "again.dxf");
        DxfExport.Write(dxf, DxfExport.Analyze(Assert.Single(board.CreatedCellDirs), tech, 1000), new DxfExportOptions(InsUnits: DxfUnits.Millimeters));

        var again = Import(dxf, "second", tech, null);
        Assert.Equal(2, again.CreatedCellDirs.Count);
        var cell = again.CreatedCellDirs.Select(d => LayoutPersistence.LoadFromFile(
                Directory.GetFiles(CellFolder.SubFolderPath(d, ViewType.Layout), "*.clay").Single()))
            .Single(v => v.Shapes.Count > 0);

        var after = cell.Shapes.OfType<PolygonShape>().Where(p => p.Layer == top).ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Sum(p => p.Holes?.Count ?? 0), after.Sum(p => p.Holes?.Count ?? 0));
    }

    /// <summary>Wire bonds leave a DXF by their own route. Their layers are no row of the table, even with the board read.</summary>
    [Fact]
    public void WireLayers_AreNoRowOfTheTable_AndAFileWithWiresStillReadsItsCopper()
    {
        var tech = Tech();
        var top = LayerNamed(tech, "Top Copper");
        var design = new WBondDesign();
        var array = new WireArray { Name = "Sig" };
        array.Wires.Add(LoopShape.CreateSeedWire(new Point3(0, 0, 0), new Point3(1_000_000, 0, 0),
                                                 diameterNm: 25_400, material: "Gold", loopHeightNm: 200_000));
        design.Arrays.Add(array);

        string dxf = Path.Combine(_root, "wires.dxf");
        using (var writer = new StreamWriter(dxf))
        {
            var copper = new InterchangeStructure("TOP", [
                new PathShape { Layer = top, Xy = [0, 0, 5_000_000, 0], Width = 0 },
                new PathShape { Layer = top, Xy = [5_000_000, 0, 5_000_000, 5_000_000], Width = 0 },
                new PathShape { Layer = top, Xy = [5_000_000, 5_000_000, 0, 5_000_000], Width = 0 },
                new PathShape { Layer = top, Xy = [0, 5_000_000, 0, 0], Width = 0 },
            ], []);
            DxfWriter.Write(writer, [copper], "TOP", tech, 1000, new DxfExportOptions(InsUnits: DxfUnits.Millimeters), design);
        }

        IReadOnlyList<string?> names = [];
        using (var stream = File.OpenRead(dxf))
        {
            var result = DxfImport.Import(stream, Path.Combine(_root, "w"), tech, 1000, resolveMapping: request =>
            {
                names = request.Rows.Select(r => r.SourceName).ToList();
                var (rows, refusal) = DxfBoardReading.ApplyMap("Top Copper=Top Copper", request.Rows, tech);
                Assert.Null(refusal);
                return new DxfMappingAnswer(rows!);
            });
            Assert.Single(LoadOnly(result).Shapes.OfType<PolygonShape>(), p => p.Layer == top);
        }

        Assert.Equal(["Top Copper"], names);
        Assert.Single(DxfReader.Read(new StreamReader(dxf)).WirePolylines);
    }

    [Fact]
    public void TheDialogRow_OffersIgnoreOnlyToADxfRow_AndItsReadingFollowsTheTarget()
    {
        var tech = Tech();
        var source = new LayerKey(-1, 0);
        var row = new LayerMappingRow(source, "PATTERN", 5, null, LayerMatchKind.NoMatch,
            new(LayoutFragment.LayerReconciliationAction.AddToTechnology));

        var plain = LayerMappingRows.Build([row], tech, true)[0];
        Assert.DoesNotContain(plain.Actions, a => a.Action == LayoutFragment.LayerReconciliationAction.Ignore);
        Assert.False(plain.ShowReadingCombo);

        var vm = LayerMappingRows.Build([row with { Reading = DxfLayerReading.AsDrawn }], tech, true)[0];
        Assert.Contains(vm.Actions, a => a.Action == LayoutFragment.LayerReconciliationAction.Ignore);

        vm.SelectedAction = vm.Actions.First(a => a.Action == LayoutFragment.LayerReconciliationAction.MapToExisting);
        vm.SelectedMapTarget = vm.MapTargets.First(l => l.Name == "Top Copper");
        Assert.Equal(DxfLayerReading.Filled, vm.CurrentReading);

        vm.SelectedMapTarget = vm.MapTargets.First(l => l.Name == "Drill");
        Assert.Equal(DxfLayerReading.Vias, vm.CurrentReading);

        vm.SelectedMapTarget = vm.MapTargets.First(l => l.Name == "Silk Top");
        Assert.False(vm.ShowReadingCombo);
        Assert.Equal(DxfLayerReading.AsDrawn, vm.CurrentReading);
    }

    [Fact]
    public void TheCli_RefusesALayerTheMapDoesNotName_ListingTheLayers()
    {
        var (code, _, stderr) = CliProcess.Run(_root, [], "convert", Outlines(), "-o", Path.Combine(_root, "r"), "--to", "clay",
            "--tech", TechId, "--layer-map", "PATTERN=Top Copper");
        Assert.Equal(1, code);
        Assert.Contains("The layer map does not name", stderr, StringComparison.Ordinal);
        Assert.Contains("the layers model space reaches: PATTERN, ", stderr, StringComparison.Ordinal);
        foreach (string layer in new[] { "HOLE", "PARTS", "SHELL" }) Assert.Contains(layer, stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "r")));
    }

    /// <summary>The whole route headlessly: convert, check, then an edge port on the line reaches the dry run with a return.</summary>
    [Fact]
    public void TheCli_ImportsTheBoard_WhichChecksClean_AndAPortOnTheLineReachesADryRun()
    {
        string target = Path.Combine(_root, "w");
        var (code, _, stderr) = CliProcess.Run(_root, [], "convert", Outlines(), "-o", target, "--to", "clay",
            "--tech", TechId, "--layer-map", BoardMap);
        Assert.True(code == 0, stderr);

        var (checkCode, checkOut, checkErr) = CliProcess.Run(_root, [], "check", target);
        Assert.True(checkCode == 0, checkOut + checkErr);

        var tech = TechPersistence.LoadFromFile(Directory.GetFiles(target, "*.ctech").Single());
        var clay = Directory.GetFiles(target, "*.clay", SearchOption.AllDirectories).Single();
        var view = LayoutPersistence.LoadFromFile(clay);
        var top = LayerNamed(tech, "Top Copper");
        view.Shapes.Add(new LabelShape { Layer = top, X = 7_000_000, Y = 5_000_000, Text = "1", Height = 200_000, IsPort = true });
        view.Shapes.Add(new LabelShape { Layer = top, X = 13_000_000, Y = 5_000_000, Text = "2", Height = 200_000, IsPort = true });
        LayoutPersistence.SaveToFile(clay, view);

        WorkspacePersistence.SaveToFile(Path.Combine(target, ".cws"),
            new CwsFile { DefaultTechRef = Path.GetFileName(Directory.GetFiles(target, "*.ctech").Single()) });
        string cem = Path.Combine(target, "b.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup
        {
            Name = "b", LayoutRef = Path.GetRelativePath(target, clay),
            Frequency = new FrequencySpec("0.1", "0.5", 3, SweepKind.Linear, "GHz", "GHz"),
        });

        var (dryCode, dryOut, dryErr) = CliProcess.Run(_root, [], "em", cem, "--dry-run");
        Assert.True(dryCode == 0, dryOut + dryErr);
        Assert.Contains("return plane Bottom Copper", dryOut + dryErr, StringComparison.Ordinal);
        Assert.Contains("Port 1 ('1')", dryOut + dryErr, StringComparison.Ordinal);
    }
}
