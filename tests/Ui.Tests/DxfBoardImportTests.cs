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
    private const string TechId = "pcb-2layer_RO4350B_20mil_1oz";

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
