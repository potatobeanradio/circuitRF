using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-bsp-4 — a board brought in from a file, or laid out from a schematic, keeps every part on the face
/// it was on, with one footprint cell per footprint whichever face it is used on. One test per claim.
/// </summary>
public sealed class BottomSideRoundTripTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crfBottomTrip_").FullName;

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_ws);
        try { Directory.Delete(_ws, recursive: true); } catch { /* best effort */ }
    }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private const string TwoLayer = "pcb-2layer_RO4350B_20mil_1oz";

    private static string Fixture(string name)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "testdata", "pcb-samples", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name);
    }

    private (PcbImport.ImportResult Result, LayoutView Board, string BoardLayoutDir) Import(
        string path, string into, Technology? tech, ImportTechnologyTarget? target = null)
    {
        string dir = Path.Combine(_ws, into);
        Directory.CreateDirectory(dir);
        using var stream = File.OpenRead(path);
        var r = PcbImport.Import(stream, dir, "board", tech, Dbu, target: target);
        Assert.False(r.Cancelled);
        string layoutDir = CellFolder.SubFolderPath(r.BoardCellDir!, ViewType.Layout);
        return (r, LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, "board.clay")), layoutDir);
    }

    /// <summary>
    /// The import with no technology to find bottom layers in, so every back part keeps its own baked cell — the
    /// oracle the bottom placements are compared against. Into an existing, EMPTY technology: an import that writes
    /// its own technology (issue #34) always has one to look the bottom layers up in.
    /// </summary>
    private (PcbImport.ImportResult Result, LayoutView Board, string BoardLayoutDir) Baked(string path)
    {
        string empty = Path.Combine(_ws, "empty.ctech");
        TechPersistence.SaveToFile(empty, new Technology { Name = "empty" });
        return Import(path, "old", null, ImportTechnologyTarget.Use(empty));
    }

    /// <summary>The technology an import leaves behind — the destination plus what it adds, as convert mints it.</summary>
    private static Technology After(Technology dest, PcbImport.ImportResult r)
    {
        var t = TechPersistence.Clone(dest);
        foreach (var def in r.LayersToAdd)
            if (t.Layers.All(l => l.Key != def.Key)) t.Layers.Add(def);
        if (r.Stackup is { } s && t.Stackup.Layers.Count == 0) t.Stackup = s;
        return t;
    }

    /// <summary>Every shape one placement draws, in world space, as layer + geometry that does not depend on how
    /// the geometry is written down (a ring's start and winding, a rect's corner order).</summary>
    private static List<string> Lands(LayoutInstance inst, string boardLayoutDir, Technology? tech)
    {
        var flat = LayoutFlatten.FlattenOneLevel(inst, boardLayoutDir, tech)!.Shapes;
        return [.. flat.Select(s => $"{s.Layer.Layer}/{s.Layer.Datatype}|{Geometry(s)}").Order(StringComparer.Ordinal)];
    }

    private static string Geometry(LayoutShape s) => s switch
    {
        RectShape r => "ring " + Ring([r.X1, r.Y1, r.X2, r.Y1, r.X2, r.Y2, r.X1, r.Y2]),
        PolygonShape p => "ring " + Ring(p.Xy) + string.Concat((p.Holes ?? []).Select(h => " hole " + Ring(h)).Order()),
        CircleShape c => $"circle {c.Cx},{c.Cy},{c.R}",
        ViaShape v => $"via {v.X},{v.Y},{v.PadSize},{v.DrillSize}",
        PathShape pa => $"path w{pa.Width} " + string.Join(",", Enumerable.Range(0, pa.Xy.Length / 2)
                            .Select(i => (pa.Xy[2 * i], pa.Xy[2 * i + 1])).Order()),
        _ => s.GetType().Name + " " + LayoutGeometry.BboxOf(s),
    };

    private static string Ring(long[] xy)
    {
        var pts = Enumerable.Range(0, xy.Length / 2).Select(i => (xy[2 * i], xy[2 * i + 1])).ToList();
        double area = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[(i + 1) % pts.Count]);
            area += (double)a.Item1 * b.Item2 - (double)b.Item1 * a.Item2;
        }
        if (area < 0) pts.Reverse();
        int start = pts.IndexOf(pts.Min());
        return string.Join(" ", pts.Skip(start).Concat(pts.Take(start)));
    }

    private static LayoutInstance Part(LayoutView board, string refdes) => board.Instances.Single(i => i.RefDes == refdes);

    // ── R-bsp4-1 / R-bsp4-2: one cell per footprint, Side on the placement, every land where it was ──────────

    [Fact]
    public void TwoSidedBoard_BackPartsAreBottomPlacementsOfTheFrontCell_EveryLandExactlyWhereTheBakedImportPutIt()
    {
        // The import as it always was: with no technology to find bottom layers in, every back part keeps its
        // own baked cell — the oracle every land below is compared against, to the DBU.
        var old = Baked(Fixture("two-sided.kicad_pcb"));
        var dest = new Technology { Name = "board" };
        var now = Import(Fixture("two-sided.kicad_pcb"), "new", dest);
        var tech = After(dest, now.Result);

        string[] back = ["R40", "R50", "Q2", "Q3", "J2"];
        foreach (string refdes in back)
        {
            var inst = Part(now.Board, refdes);
            Assert.Equal(PlacementSide.Bottom, inst.Side);
            Assert.True(inst.MirrorX);
            Assert.Equal(Lands(Part(old.Board, refdes), old.BoardLayoutDir, null), Lands(inst, now.BoardLayoutDir, tech));
        }
        foreach (string refdes in new[] { "R10", "Q1", "J1" })
            Assert.Equal(Lands(Part(old.Board, refdes), old.BoardLayoutDir, null), Lands(Part(now.Board, refdes), now.BoardLayoutDir, tech));

        // One cell per footprint: each back part shares its front part's cell. The through-hole part is symmetric
        // in Y with the same copper on both faces, so its baked flip was already its front cell; the other two
        // footprints used on both faces are two cells fewer.
        Assert.Equal(Part(now.Board, "R10").CellRef, Part(now.Board, "R40").CellRef);
        Assert.Equal(Part(now.Board, "Q1").CellRef, Part(now.Board, "Q3").CellRef);
        Assert.Equal(Part(now.Board, "J1").CellRef, Part(now.Board, "J2").CellRef);
        Assert.Equal(old.Result.CreatedCellDirs.Count - 2, now.Result.CreatedCellDirs.Count);

        // The designator the author placed is drawn where it was.
        var label = FootprintLabel.PlacementFor(Part(now.Board, "R40"), null, null, Dbu)!.Value;
        var was = FootprintLabel.PlacementFor(Part(old.Board, "R40"), null, null, Dbu)!.Value;
        Assert.Equal((was.X, was.Y), (label.X, label.Y));

        Assert.Contains(now.Result.Messages, m => m.StartsWith("6 back-side placement(s) found; 5 placed on the bottom", StringComparison.Ordinal));
    }

    // ── R-bsp4-3: what cannot be un-flipped stays as it was ─────────────────────────────────────────────────

    [Fact]
    public void ABackRoleTheTechnologyHasNoFrontLayerFor_KeepsItsBakedCellOnTheTop_AndTheReportNamesTheLayer()
    {
        var old = Baked(Fixture("two-sided.kicad_pcb"));
        var now = Import(Fixture("two-sided.kicad_pcb"), "new", new Technology { Name = "board" });

        // Nothing on the board draws on a front assembly layer, so B.Fab has no front counterpart.
        var u9 = Part(now.Board, "U9");
        Assert.Equal(PlacementSide.Top, u9.Side);
        Assert.False(u9.MirrorX);
        Assert.Equal(Part(old.Board, "U9").RotationDegrees, u9.RotationDegrees);
        Assert.Equal(Lands(Part(old.Board, "U9"), old.BoardLayoutDir, null), Lands(u9, now.BoardLayoutDir, null));
        Assert.Contains(now.Result.Messages, m => m.Contains("'fixture:FAB_ONLY' (U9)") && m.Contains("'B.Fab'"));
    }

    // ── R-bsp4-2: export (BSP-3) → import ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ExportThenImport_ATwoSidedBoardComesBackWithItsSides_OneCell_LandsInPlace()
    {
        var t = ShippedTechnologies.Load(TwoLayer);
        LayerKey K(string name) => t.Layers.Single(l => l.Name == name).Key;
        string design = Path.Combine(_ws, "design");
        string chip = CellFolder.CreateCellFolder(design, "Chip");
        var chipView = new LayoutView { DbuPerMicron = Dbu };
        chipView.Shapes.Add(new RectShape { Layer = K("Top Copper"), X1 = -900_000, Y1 = -100_000, X2 = -500_000, Y2 = 300_000 });
        chipView.Shapes.Add(new RectShape { Layer = K("Top Copper"), X1 = 400_000, Y1 = -300_000, X2 = 800_000, Y2 = 100_000 });
        chipView.Shapes.Add(new RectShape { Layer = K("Silk Top"), X1 = -900_000, Y1 = 500_000, X2 = 800_000, Y2 = 600_000 });
        chipView.Pins.Add(new LayoutPin { Name = "1", X = -700_000, Y = 100_000, WidthDbu = 400_000, Layer = K("Top Copper") });
        chipView.Pins.Add(new LayoutPin { Name = "2", X = 600_000, Y = -100_000, WidthDbu = 400_000, Layer = K("Top Copper") });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(chip, ViewType.Layout), "main.clay"), chipView);

        string boardDir = CellFolder.CreateCellFolder(design, "Board");
        var boardView = new LayoutView { DbuPerMicron = Dbu };
        boardView.Instances.Add(new LayoutInstance { CellRef = "../../Chip", X = 4_000_000, Y = 3_000_000, RotationDegrees = 30, RefDes = "R1" });
        boardView.Instances.Add(new LayoutInstance { CellRef = "../../Chip", X = 9_000_000, Y = 3_000_000, RotationDegrees = 30, MirrorX = true, Side = PlacementSide.Bottom, RefDes = "R2" });
        boardView.Instances.Add(new LayoutInstance { CellRef = "../../Chip", X = 14_000_000, Y = 3_000_000, RotationDegrees = 90, MirrorX = true, Side = PlacementSide.Bottom, RefDes = "R3" });
        string boardLayoutDir = CellFolder.SubFolderPath(boardDir, ViewType.Layout);
        LayoutPersistence.SaveToFile(Path.Combine(boardLayoutDir, "main.clay"), boardView);

        var plan = PcbExport.Analyze(boardDir, t, Dbu);
        Assert.True(plan.CanWrite, plan.Refusal);
        string file = Path.Combine(_ws, "out.kicad_pcb");
        PcbExport.Write(file, plan);

        var back = Import(file, "back", t);
        var after = After(t, back.Result);
        foreach (var original in boardView.Instances)
        {
            var read = Part(back.Board, original.RefDes!);
            Assert.Equal(original.Side, read.Side);
            Assert.Equal(original.MirrorX, read.MirrorX);
            Assert.Equal(original.RotationDegrees, read.RotationDegrees, 6);
            Assert.Equal(Part(back.Board, "R1").CellRef, read.CellRef);

            // Copper lands where circuitRF drew them, on the same face — to a DBU or two of millimetre rounding.
            static IEnumerable<(long, long, long, long)> Copper(IEnumerable<LayoutShape> shapes, LayerKey layer) =>
                shapes.Where(s => s.Layer == layer).Select(LayoutGeometry.BboxOf)
                      .Select(b => (b.MinX / 10, b.MinY / 10, b.MaxX / 10, b.MaxY / 10)).Order();
            var drawn = LayoutFlatten.FlattenOneLevel(original, boardLayoutDir, t)!.Shapes;
            var again = LayoutFlatten.FlattenOneLevel(read, back.BoardLayoutDir, after)!.Shapes;
            var face = original.Side == PlacementSide.Bottom ? K("Bottom Copper") : K("Top Copper");
            Assert.Equal(2, Copper(drawn, face).Count());
            foreach (var (a, b) in Copper(drawn, face).Zip(Copper(again, face)))
                Assert.True(Math.Abs(a.Item1 - b.Item1) <= 1 && Math.Abs(a.Item2 - b.Item2) <= 1 &&
                            Math.Abs(a.Item3 - b.Item3) <= 1 && Math.Abs(a.Item4 - b.Item4) <= 1, $"{original.RefDes}: {a} vs {b}");
        }
        Assert.Equal(2, back.Result.CreatedCellDirs.Count);   // the one footprint, and the board
    }

    // ── R-bsp4-5 / R-bsp4-6: the schematic round trip ───────────────────────────────────────────────────────

    private (SchematicEditModel Schematic, EditableComponent R1, LayoutView Layout, Technology Tech, string TechPath,
             string SchematicDir, string LayoutDir) LinkedResistor()
    {
        var tech = ShippedTechnologies.Load(TwoLayer);
        Directory.CreateDirectory(Path.Combine(_ws, "tech"));
        string techPath = Path.Combine(_ws, "tech", "t.ctech");
        TechPersistence.SaveToFile(techPath, tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });
        string cellDir = CellFolder.CreateCellFolder(_ws, "Amp");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var schematic = new SchematicEditModel { SchematicDirectory = schematicDir };
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        r1.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402@N" });
        schematic.Components.Add(r1);
        var layout = new LayoutView();
        var ctx = (schematic, r1, layout, tech, techPath, schematicDir, layoutDir);
        Forward(ctx);
        return ctx;
    }

    private void Forward((SchematicEditModel Schematic, EditableComponent R1, LayoutView Layout, Technology Tech, string TechPath,
                          string SchematicDir, string LayoutDir) c)
        => SchematicToLayoutGenerator.Run(c.Schematic, c.Layout, c.SchematicDir, _ws, c.LayoutDir, c.Tech, c.TechPath, null)
                                     .Command?.Execute();

    /// <summary>Flip's own bookkeeping (<c>LayoutEditorViewModel.SetSelectedInstancesSide</c>): the reflection about
    /// the vertical axis through the part's origin, and the other face.</summary>
    private static void Flip(LayoutInstance inst)
    {
        inst.RotationDegrees = LayoutAngle.Normalize(-inst.RotationDegrees);
        inst.MirrorX = !inst.MirrorX;
        inst.Side = inst.Side == PlacementSide.Top ? PlacementSide.Bottom : PlacementSide.Top;
    }

    [Fact]
    public void AFlipInTheLayout_IsNotAMirrorToTheLink_UpdateLayoutLeavesThePartAndTheSymbolAlone()
    {
        var c = LinkedResistor();
        var inst = c.Layout.Instances.Single();
        Assert.NotEqual(0.0, inst.RotationDegrees % 180.0);   // off the horizontal, where a flip also turns the part
        var facing = SchematicLayoutOrientation.FromLayout(inst);

        Flip(inst);
        Assert.True(SchematicLayoutOrientation.FromLayout(inst).SameAs(facing));
        var flipped = (inst.MirrorX, inst.RotationDegrees, inst.Side);

        LayoutToSchematicGenerator.Run(c.Layout, c.Schematic, c.LayoutDir, c.Tech).Command?.Execute();
        Assert.Equal((SymbolRotation.R0, false), (c.R1.Rotation, c.R1.MirrorX));

        Forward(c);
        var now = c.Layout.Instances.Single();
        Assert.Equal(flipped, (now.MirrorX, now.RotationDegrees, now.Side));
    }

    [Fact]
    public void MirroringTheSymbol_MirrorsTheBottomPartOnItsOwnFace()
    {
        var c = LinkedResistor();
        var inst = c.Layout.Instances.Single();
        var (m0, deg0) = (inst.MirrorX, inst.RotationDegrees);
        Flip(inst);

        c.R1.MirrorX = true;
        Forward(c);

        // The facing seen on the bottom took the symbol's left-right mirror: the flipped part (mirrored, at -deg0)
        // is reflected about its own vertical axis on the bottom face — not un-flipped, and not left alone.
        var now = c.Layout.Instances.Single();
        Assert.Equal(PlacementSide.Bottom, now.Side);
        Assert.Equal(m0, now.MirrorX);
        Assert.Equal(deg0, now.RotationDegrees, 6);
    }

    [Fact]
    public void AFootprintChangeThatRegeneratesTheLandPattern_KeepsTheBottomPartOnTheBottom()
    {
        var c = LinkedResistor();
        var inst = c.Layout.Instances.Single();
        Flip(inst);
        string cellRef = inst.CellRef;

        c.R1.Parameters.Single(p => p.Name == "Footprint").Expression = "smt:0603@N";
        Forward(c);

        var now = c.Layout.Instances.Single();
        Assert.NotEqual(cellRef, now.CellRef);
        Assert.Equal(PlacementSide.Bottom, now.Side);
        Assert.Equal(inst.MirrorX, now.MirrorX);
    }
}
