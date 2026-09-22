using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-4b-designators.md §10 — the gate for the designator a PLACEMENT draws.
///
/// <para>The thing the designer actually asked for is test 1: <b>a placed part says what it is
/// called, on the silkscreen, and the same string reaches the export</b>. At HEAD before this brief
/// there was no such field in the model and <c>LayoutRenderer.Instances</c> dropped every label
/// inside a placed instance outright, so nothing anywhere drew one.</para>
/// </summary>
public sealed class FootprintDesignatorTests : IDisposable
{
    private readonly string _root;

    public FootprintDesignatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-fp4b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    // ══ 1. It draws at all, and the text is the SchematicId ═════════════════════════════════════

    [Fact]
    public void APlacedPartDrawsItsDesignatorOnSilkAndItIsTheSchematicId()
    {
        var (board, layoutDir, tech) = Board("Board1");
        board.Instances.Add(Place("smt:0402@N", "R7", 0, 0));

        var silk = Silk(tech);
        var labels = FootprintLabel.ShapesFor(board, layoutDir, tech, null);

        var label = Assert.Single(labels);
        Assert.Equal("R7", label.Text);
        Assert.Equal(silk, label.Layer);

        // Nothing was stored to make that true — the text is DERIVED (R-fp4b-1a).
        Assert.Null(board.Instances[0].RefDes);

        // And the RENDERER draws it. A differential render is the oracle: the only difference
        // between the two frames is one placement's ShowRefDes, so any difference in the picture is
        // the designator and nothing else.
        string shown = Render(board, tech, layoutDir);
        board.Instances[0].ShowRefDes = false;
        string hidden = Render(board, tech, layoutDir);
        Assert.NotEqual(shown, hidden);
    }

    // ══ 2. Derived, not stored — a rename follows with nothing written ══════════════════════════

    [Fact]
    public void RenamingTheSchematicComponentMovesTheDesignatorWithNothingWrittenToRefDes()
    {
        var (board, layoutDir, tech) = Board("Board2");
        var inst = Place("smt:0402@N", "C3", 0, 0);
        board.Instances.Add(inst);
        Assert.Equal("C3", Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).Text);

        // What Update Layout does on a rename: it rewrites SchematicId in place. No migration, no
        // second write path, and nothing for the two fields to disagree about.
        inst.SchematicId = "C12";

        Assert.Equal("C12", Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).Text);
        Assert.Null(inst.RefDes);
    }

    // ══ 3. Auto is RECOMPUTED, not frozen ═══════════════════════════════════════════════════════

    [Fact]
    public void RePointingAPartAtABiggerCaseMovesItsDesignatorClearOfTheNewBody()
    {
        var (board, layoutDir, tech) = Board("Board3");
        var inst = Place("smt:0402@N", "C1", 0, 0);
        board.Instances.Add(inst);
        long small = Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).Y;

        // R-fp3-4a's supported gesture. Nothing in the .clay changed but the CellRef.
        inst.CellRef = GeneratedCell(layoutDir, "smt:0805@N", tech);
        long big = Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).Y;

        // The 0805's body is wider than the 0402's, so a default that had been FROZEN at placement
        // time would leave the designator sitting inside it — correct when written, wrong afterwards,
        // and wrong silently.
        Assert.True(big > small, $"0805 designator at {big} should clear the 0402's {small}");
        Assert.True(big > BodyTop(layoutDir, inst.CellRef),
                    "the designator sits inside the 0805's own silkscreen body");
    }

    // ══ 4. Never upside down, never mirrored ════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.0, false)] [InlineData(90.0, false)] [InlineData(180.0, false)]
    [InlineData(270.0, false)] [InlineData(37.0, false)]
    [InlineData(180.0, true)] [InlineData(37.0, true)]
    public void TheDrawnAngleIsAlwaysReadableAndTheTextIsNeverMirrored(double degrees, bool mirror)
    {
        var (board, layoutDir, tech) = Board("Board4");
        var inst = Place("smt:0402@N", "U1", 0, 0);
        inst.RotationDegrees = degrees;
        inst.MirrorX = mirror;
        board.Instances.Add(inst);

        var label = Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null));
        double a = label.RotationDegrees > 180.0 ? label.RotationDegrees - 360.0 : label.RotationDegrees;
        Assert.InRange(a, -90.0 + 1e-9, 90.0);

        // A LabelShape carries no mirror of its own, so the un-mirrored glyphs are structural — this
        // pins that nothing was added that could reverse them, at the MIRRORED anchor (R-fp4b-3b).
        Assert.DoesNotContain(label.GetType().GetProperties(), p => p.Name.Contains("Mirror", StringComparison.Ordinal));
    }

    // ══ 5. One source, four consumers ═══════════════════════════════════════════════════════════

    [Fact]
    public void TheSameDesignatorReachesGerbersFlattenGdsiiDxfAndTheBoardExportsReferenceProperty()
    {
        var (board, layoutDir, tech) = Board("Board5");
        board.Instances.Add(Place("smt:0402@N", "R1", 0, 0));
        string cellDir = Path.GetDirectoryName(layoutDir)!;
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "Board5.clay"), board);

        var silk = Silk(tech);

        // Gerber and DRC and `check` — the whole-design flatten.
        var flat = LayoutDesignFlatten.Flatten(board, cellDir, tech, null, null);
        Assert.Contains(flat.Shapes.OfType<LabelShape>(), l => l.Text == "R1" && l.Layer == silk);

        // GDSII and DXF — hierarchical, so the ROOT structure carries its own placements' designators.
        Assert.Contains(GdsiiExport.Analyze(cellDir, tech, board.DbuPerMicron, board).Structures[0].Shapes.OfType<LabelShape>(),
                        l => l.Text == "R1" && l.Layer == silk);
        Assert.Contains(DxfExport.Analyze(cellDir, tech, board.DbuPerMicron, board).Structures[0].Shapes.OfType<LabelShape>(),
                        l => l.Text == "R1" && l.Layer == silk);

        // The board format carries it as a PROPERTY, not as flattened outlines — the receiving tool
        // owns how it draws its own silkscreen text (R-fp4b-8b).
        var plan = PcbExport.Analyze(cellDir, tech, board.DbuPerMicron, board);
        Assert.Equal("R1", Assert.Single(plan.Model.Placements).Reference);
    }

    // ══ 6. EM does not see it ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void AnEmExtractionIsIdenticalWithAndWithoutDesignators()
    {
        var (board, layoutDir, _) = Board("Board6");
        board.Instances.Add(Place("smt:0402@N", "R1", 0, 0));
        string clay = Path.Combine(layoutDir, "Board6.clay");
        LayoutPersistence.SaveToFile(clay, board);

        int withDesignator = EmGeometry.Flatten(board, clay).Shapes.Count;
        board.Instances[0].SchematicId = null;
        int without = EmGeometry.Flatten(board, clay).Shapes.Count;

        Assert.Equal(without, withDesignator);
    }

    // ══ 7. No silk, no designator, and NOT relocated ════════════════════════════════════════════

    [Fact]
    public void ATechnologyWithNoSilkscreenDrawsNoDesignatorAnywhereElse()
    {
        var (board, layoutDir, tech) = Board("Board7");
        board.Instances.Add(Place("smt:0402@N", "R1", 0, 0));

        var noSilk = StripSilk(tech);
        Assert.Empty(FootprintLabel.ShapesFor(board, layoutDir, noSilk, null));

        // And the diagnostic names the technology, through the channel LandPatternLayers already uses.
        var diagnostics = new List<string>();
        LandPatternLayers.Resolve(noSilk, PCellLayerSelection.Default, diagnostics);
        Assert.Contains(diagnostics, d => d.Contains("silkscreen", StringComparison.OrdinalIgnoreCase)
                                       && d.Contains(noSilk.Name, StringComparison.Ordinal));
    }

    // ══ 8. Additive — an untouched .clay re-saves byte for byte ═════════════════════════════════

    [Fact]
    public void AClayWrittenBeforeThisBriefLoadsAndReSavesByteForByte()
    {
        var (board, layoutDir, _) = Board("Board8");
        board.Instances.Add(Place("smt:0402@N", "R1", 0, 0));
        string path = Path.Combine(layoutDir, "Board8.clay");
        LayoutPersistence.SaveToFile(path, board);

        byte[] first = File.ReadAllBytes(path);
        var reloaded = LayoutPersistence.LoadFromFile(path);
        LayoutPersistence.SaveToFile(path, reloaded);

        Assert.Equal(first, File.ReadAllBytes(path));
        Assert.DoesNotContain("ShowRefDes", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
        Assert.DoesNotContain("LabelDx", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    // ══ 9. The default, asserted explicitly ═════════════════════════════════════════════════════

    [Fact]
    public void AnInstanceThatSetsNothingDrawsItsDesignator()
    {
        // The open decision in §8, confirmed with the owner 2026-09-20: SHOWN. Stated here as a fact
        // rather than left to emerge, so the day it is reconsidered this test is what has to change.
        var inst = new LayoutInstance { CellRef = "x", SchematicId = "R1" };
        Assert.Null(inst.ShowRefDes);
        Assert.True(inst.DesignatorShown);
    }

    // ══ 10. Drag, undo, reset ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void OneDragIsOneUndoEntryAndResetGoesBackToDerivedNotToThePreDragPosition()
    {
        var (board, layoutDir, tech) = Board("Board10");
        board.Instances.Add(Place("smt:0402@N", "R1", 0, 0));
        string clay = Path.Combine(layoutDir, "Board10.clay");
        LayoutPersistence.SaveToFile(clay, board);

        var vm = new LayoutEditorViewModel(board, clay) { Technology = tech };
        var auto = Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null));

        // Grab the designator where it actually is, and drag it 300 um right.
        vm.OnPointerPressed(auto.X, auto.Y, Avalonia.Input.KeyModifiers.None, hitTolDbu: 200);
        Assert.Equal([0], vm.SelectedDesignatorIndices);
        vm.OnPointerMoved(auto.X + 300_000, auto.Y, leftDown: true, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(auto.X + 300_000, auto.Y, Avalonia.Input.KeyModifiers.None);

        Assert.NotNull(board.Instances[0].LabelDx);
        long dragged = Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).X;
        Assert.Equal(auto.X + 300_000, dragged);

        // ONE undo entry for the whole gesture.
        vm.UndoCommand.Execute(null);
        Assert.Null(board.Instances[0].LabelDx);
        Assert.Equal(auto.X, Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).X);

        // Redo it, then Reset — back to DERIVED, not back to a remembered number. The difference is
        // visible because the footprint changes underneath in between: a reset that restored a stored
        // value would put the designator where the 0402's body used to be.
        vm.RedoCommand.Execute(null);
        Assert.True(vm.CanResetSelectedDesignatorPositions);
        vm.ResetSelectedDesignatorPositions();
        Assert.Null(board.Instances[0].LabelDx);
        Assert.Null(board.Instances[0].LabelDy);
        Assert.Equal(auto.X, Assert.Single(FootprintLabel.ShapesFor(board, layoutDir, tech, null)).X);
    }

    // ══ 11. A board round trip ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ABoardsPlacementsImportWithTheirDesignatorsWhereTheirAuthorPutThem()
    {
        var result = PcbReader.Read(ThreePlacementBoard, LayoutUnits.DefaultDbuPerMicron);
        Assert.Null(result.Refusal);
        Assert.NotNull(result.Board);

        Assert.Equal(3, result.Board!.Placements.Count);
        Assert.Equal(["C10", "R3", "U1"], result.Board!.Placements.Select(p => p.Reference).OrderBy(r => r, StringComparer.Ordinal));

        // R3's own (at 0 -1.5) — the Y flips up, and X does not (PcbUnits' one and only handedness flip).
        var r3 = result.Board!.Placements.Single(p => p.Reference == "R3");
        Assert.Equal(0, r3.LabelDx);
        Assert.Equal(1_500_000, r3.LabelDy);

        // U1 states no position for its designator, so it stays AUTO — null, never zero, or a part
        // whose author never moved its designator would stop following its own body.
        Assert.Null(result.Board!.Placements.Single(p => p.Reference == "U1").LabelDx);
    }

    // ══ 12. A duplicate is reported, never renumbered ═══════════════════════════════════════════

    [Fact]
    public void TwoPlacementsSharingADesignatorAreReportedAndBothStayAsAuthored()
    {
        var (board, layoutDir, tech) = Board("Board12");
        board.Instances.Add(Place("smt:0402@N", "C3", 0, 0));
        board.Instances.Add(Place("smt:0402@N", "C3", 2_000_000, 0));

        string report = Assert.Single(FootprintLabel.DuplicateReports(board));
        Assert.Contains("C3", report, StringComparison.Ordinal);

        // Both still drawn, as authored.
        var labels = FootprintLabel.ShapesFor(board, layoutDir, tech, null);
        Assert.Equal(2, labels.Count);
        Assert.All(labels, l => Assert.Equal("C3", l.Text));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private (LayoutView Board, string LayoutDir, Technology Tech) Board(string cellName)
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        string cellDir = CellFolder.CreateCellFolder(_root, cellName);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        return (new LayoutView { TechRef = "../../tech/t.ctech" }, layoutDir, tech);
    }

    /// <summary>A schematic-generated placement of a built-in land pattern — SchematicId set, which
    /// is the state every Update Layout instance has carried since L5.</summary>
    private LayoutInstance Place(string footprint, string schematicId, long x, long y)
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        string cellRef = GeneratedCell(null, footprint, tech);
        return new LayoutInstance { CellRef = cellRef, X = x, Y = y, Mag = 1.0, SchematicId = schematicId };
    }

    /// <summary>Generates the land-pattern cell on disk and returns a reference to it from a board's
    /// own <c>layout/</c> folder — <c>GeneratedCellStore</c>, the one the editor itself uses.</summary>
    private string GeneratedCell(string? layoutDir, string footprint, Technology tech)
    {
        Assert.True(FootprintRef.TryParse(footprint, out var reference, out _));
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, reference!.ToString(), new Dictionary<string, PCellValue>(), tech,
            Path.Combine(_root, "tech", "t.ctech"), PCellLayerSelection.Default);
        return layoutDir is null ? cellDir : Path.GetRelativePath(layoutDir, cellDir);
    }

    private static LayerKey Silk(Technology tech)
    {
        var silk = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []).Silkscreen;
        Assert.NotNull(silk);
        return silk!.Value;
    }

    /// <summary>The technology with its silkscreen layer taken out — a board technology that declares
    /// no silk, which the Power Rail example's own is until brief 5 adds one.</summary>
    private static Technology StripSilk(Technology tech)
    {
        var silk = Silk(tech);
        var stripped = TechPersistence.Deserialize(TechPersistence.Serialize(tech));
        stripped.Layers.RemoveAll(l => l.Key.Equals(silk));
        return stripped;
    }

    /// <summary>The top of the resolved cell's own silkscreen body, in the parent's DBU.</summary>
    private static long BodyTop(string layoutDir, string cellRef)
    {
        var view = CellLayoutResolver.Resolve(cellRef, layoutDir).View!;
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        return bb.MaxY;
    }

    private static string Render(LayoutView view, Technology tech, string layoutDir)
    {
        using var stream = new SKDynamicMemoryWStream();
        var vp = new LayoutViewport(-2_000_000, -2_000_000, 400.0 / 4_000_000, 400, 400);
        using (var canvas = SKSvgCanvas.Create(SKRect.Create(0, 0, 400, 400), stream))
            LayoutRenderer.Draw(canvas, view, tech, vp,
                new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = layoutDir });
        using var data = stream.DetachAsData();
        return Encoding.UTF8.GetString(data.ToArray());
    }

    /// <summary>Three placements, two of which state where their designator goes. Anonymised to the
    /// SHAPE of a real board file, not a copy of one.</summary>
    private const string ThreePlacementBoard = """
        (kicad_pcb (version 20221018) (generator test)
          (footprint "R_0402" (layer "F.Cu") (at 10 20)
            (property "Reference" "R3" (at 0 -1.5 0))
            (pad "1" smd rect (at -0.5 0) (size 0.6 0.6) (layers "F.Cu")))
          (footprint "C_0402" (layer "F.Cu") (at 15 20 90)
            (property "Reference" "C10" (at 0 -1.5 90))
            (pad "1" smd rect (at -0.5 0) (size 0.6 0.6) (layers "F.Cu")))
          (footprint "SOIC-8" (layer "F.Cu") (at 25 30)
            (property "Reference" "U1")
            (pad "1" smd rect (at -2 -1) (size 1 0.6) (layers "F.Cu")))
        )
        """;
}
