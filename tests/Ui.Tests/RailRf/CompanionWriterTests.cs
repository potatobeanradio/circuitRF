// ================================================================
//  CompanionWriterTests.cs — brief-authored-board-3-companion-writers.md §5, gates 1-4, 7 and 9.
//
//  ── WHAT THE ROUND TRIP IS ACTUALLY FOR ───────────────────────────────────────────────────────
//
//  Writing a file and reading it back with the writer's OWN inverse proves only that the two agree
//  with each other, which is why gate 4 exists and is the one that matters: the three files are
//  written from briefs 1 and 2's projection, imported as if they were a foreign board, and the
//  WHOLE DataSet is compared against the same board analysed directly off its artwork. A swapped
//  pad pair passes every field-by-field comparison — the part still bridges the two nets, still
//  appears in the netlist, still contributes its capacitance — and moves PdnMountingLoop's
//  power/return sides, which on an asymmetric part is a wrong inductance that looks entirely
//  normal (overview §1c). Only the two answers disagreeing catches it.
//
//  ── THE FIXTURE'S PINS ARE OFF BOTH AXES AND ITS TWO PARTS ARE NOT SYMMETRIC ──────────────────
//
//  LayoutPadsTests' own rule, and it is the WB-C trap in its third form: a mirror is a no-op on a
//  symmetric land, and a pad pair swapped on one is the same board. So the land below is off both
//  axes and the two parts sit at different rotations.
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class CompanionWriterTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-ab3-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Mid = new(3, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Barrel = new(10, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // OFF BOTH AXES — see this file's header.
    private static readonly (string Name, long X, long Y)[] LandPins =
        [("1", Um(-400), Um(250)), ("2", Um(400), Um(-150))];

    // ══ 1-3. The round trip, per writer, with its evidence and its absences ════════════════════

    /// <summary>
    /// <b>Gates 1, 2 and 3 together, because they are three assertions about ONE round trip</b> and
    /// splitting them would write the board three times to ask three questions of the same bytes.
    ///
    /// <para>Gate 2's origin row is the one that matters: an unstated origin is a REFUSAL in the
    /// import dialog, and a file circuitRF wrote must never provoke that question.</para>
    /// </summary>
    [Fact]
    public void TheThreeTablesReadBackFieldForFieldWithTheirEvidenceDeclared()
    {
        var fx = Board();
        var projection = BoardCompanions.Project(fx.View, fx.Clay, fx.Tech);
        Assert.Null(projection.Refusal);

        // ── the board netlist ────────────────────────────────────────────────
        var netlist = BoardNetlistFile.Read(
            "Board.ipc", BoardCompanions.BoardNetlistTextOf(projection), Dbu);

        Assert.Null(netlist.Refusal);
        Assert.Equal(BoardNetlistUnitsEvidence.Declared, netlist.UnitsEvidence);      // gate 2
        Assert.Equal(BoardNetlistUnits.MillimetreThousandth, netlist.Units);

        foreach (var pad in projection.Pads)
        {
            var record = Assert.Single(netlist.Records,
                r => r.Component == pad.Refdes && r.Pin == pad.Pin);
            Assert.Equal(pad.Net, record.Net);
            Assert.Equal((pad.X, pad.Y), (record.X, record.Y));

            // Gate 3. A surface land states no hole and no span, so the reader must come back with
            // NULL rather than with a plausible value — a writer that filled in `1` would make a
            // through feature read as a surface one and nothing downstream would question it.
            Assert.Null(record.DrillDbu);
            Assert.Null(record.Plated);
            Assert.Null(record.Access);
            Assert.False(record.HasHole);
        }

        // The via, whose drill and span the artwork DOES state — and whose plating it does not.
        var viaRecord = Assert.Single(netlist.Records, r => r.Component is null);
        Assert.True(viaRecord.IsVia);
        Assert.Equal(Um(300), viaRecord.DrillDbu);
        Assert.Equal(0, viaRecord.Access);          // a through via, off the stackup's own span
        Assert.Null(viaRecord.Plated);              // a ViaShape states none — gate 3
        Assert.Equal("VDD", viaRecord.Net);

        // ── the placement ────────────────────────────────────────────────────
        var placement = PlacementFile.Read(
            "Board.placement.csv", BoardCompanions.PlacementTextOf(projection), Dbu);

        Assert.Null(placement.Refusal);
        Assert.Equal(PlacementOriginEvidence.Declared, placement.OriginEvidence);     // gate 2
        Assert.Equal(PlacementOrigin.SymbolOrigin, placement.Origin);
        Assert.Equal(BoardNetlistUnitsEvidence.Declared, placement.UnitsEvidence);    // gate 2
        Assert.Equal(LayoutUnit.Mm, placement.Units);

        Assert.Equal(projection.Placements.Count, placement.Rows.Count);
        foreach (var entry in projection.Placements)
        {
            var row = Assert.Single(placement.Rows, r => r.Refdes == entry.Refdes);
            Assert.Equal((entry.X, entry.Y), (row.X, row.Y));
            Assert.Equal(entry.RotationDegrees, row.RotationDegrees, 6);
            Assert.Equal(entry.Mirror, row.Mirror);
            Assert.Equal(entry.Footprint, row.Footprint);
        }

        // ── the bill of materials ────────────────────────────────────────────
        var bom = BomFile.Read("Board.bom.csv", BoardCompanions.BomTextOf(projection));

        Assert.Null(bom.Refusal);
        Assert.Equal(projection.Parts.Count, bom.Rows.Count);
        foreach (var part in projection.Parts)
        {
            var row = Assert.Single(bom.Rows, r => r.Refdes == part.Refdes);
            Assert.Equal(part.Footprint, row.Footprint);

            // R-ab3-1e. A part number is a purchasing decision circuitRF does not hold, and there
            // is no value here that would be right — so the column is not written and the reader
            // comes back null rather than with something plausible.
            Assert.Null(row.PartNumber);
        }

        output.WriteLine(
            $"{netlist.Records.Count} netlist record(s), {placement.Rows.Count} placement row(s), " +
            $"{bom.Rows.Count} bill-of-materials row(s)");
    }

    // ══ 4. The DataSet must not move ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 4, and it is the primary one.</b> Author a board, write the three files, read them
    /// back as if they were a foreign board's, and compare the WHOLE <c>DataSet</c> against the
    /// same board analysed directly through brief 1's projection. They must agree — which is what
    /// says the writers and the projection are one statement rather than two readings.
    /// </summary>
    [Fact]
    public void TheWrittenFilesAndTheProjectionAnalyseToTheSameDataSet()
    {
        var fx = Board();
        var projection = BoardCompanions.Project(fx.View, fx.Clay, fx.Tech);
        Assert.Null(projection.Refusal);

        string ipc = Path.Combine(_root, "Board.ipc");
        string place = Path.Combine(_root, "Board.placement.csv");
        string bomPath = Path.Combine(_root, "Board.bom.csv");
        BoardCompanions.Write(projection, ipc, place, bomPath);

        // The way in a FOREIGN board takes: the companion file is the evidence, and the artwork's
        // own pads are only what it does not name. R-ab1-3a's precedence means every pad below came
        // out of the file this brief wrote.
        var written = BoardNetlistFile.ReadFile(ipc, Dbu);
        Assert.NotNull(written);
        Assert.Null(written!.Refusal);

        var shapes = RailArtwork.FlattenedShapes(fx.View, fx.Clay, fx.Tech);
        var viaFile = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, written, null, shapes);
        var direct  = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, shapes);

        Assert.Equal(direct.Pads.Count, viaFile.FromBoardNetlist);   // the file named every part
        Assert.Equal(0, viaFile.FromArtwork);

        var viaFileRun = Solve(fx, shapes, viaFile);
        var directRun  = Solve(fx, shapes, direct);

        Assert.Null(viaFileRun.Refusal);
        Assert.Null(directRun.Refusal);

        var a = Assert.Single(viaFileRun.Rails);
        var b = Assert.Single(directRun.Rails);

        Assert.Equal(Flatten(b.Data), Flatten(a.Data));
        Assert.Equal(
            b.NodeVoltages.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)),
            a.NodeVoltages.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));

        output.WriteLine($"{Flatten(a.Data).Count} cube value(s) identical across both paths");
    }

    // ══ 7. One invocation, three tables that agree ═════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 7.</b> Footprint 5 R-fp5-2's governing reason: they have to agree, and hand-editing
    /// three files is how that goes wrong silently. One projection is what makes it structural.
    /// </summary>
    [Fact]
    public void EveryRefdesInThePlacementIsInTheNetlistAndInTheBillOfMaterials()
    {
        var fx = Board();
        var projection = BoardCompanions.Project(fx.View, fx.Clay, fx.Tech);

        var placement = PlacementFile.Read("p.csv", BoardCompanions.PlacementTextOf(projection), Dbu);
        var netlist = BoardNetlistFile.Read("n.ipc", BoardCompanions.BoardNetlistTextOf(projection), Dbu);
        var bom = BomFile.Read("b.csv", BoardCompanions.BomTextOf(projection));

        Assert.NotEmpty(placement.Rows);
        foreach (var row in placement.Rows)
        {
            Assert.Contains(netlist.Records, r => r.Component == row.Refdes);
            Assert.NotEmpty(bom.RowsFor(row.Refdes));
        }
    }

    // ══ 9. The thin case writes ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 9, R-ab3-2d and R-ab3-3c.</b> No schematic and no stamp: three files, no net names,
    /// and the caller SAID so. It does not refuse — a placement file with no nets in it is a
    /// perfectly useful placement file.
    /// </summary>
    [Fact]
    public void ABoardWithNoSchematicAndNoStampWritesAllThreeAndSaysWhatIsMissing()
    {
        var fx = Board(stamped: false);
        var projection = BoardCompanions.Project(fx.View, fx.Clay, fx.Tech);

        Assert.Null(projection.Refusal);
        Assert.False(projection.HasSchematic);
        Assert.False(projection.AnyNetNamed);
        Assert.Contains("no schematic", projection.ThinnessSummary, StringComparison.Ordinal);

        var netlist = BoardNetlistFile.Read("n.ipc", BoardCompanions.BoardNetlistTextOf(projection), Dbu);
        Assert.Null(netlist.Refusal);
        Assert.All(netlist.Records, r => Assert.Null(r.Net));
        Assert.NotEmpty(netlist.Records);

        var placement = PlacementFile.Read("p.csv", BoardCompanions.PlacementTextOf(projection), Dbu);
        Assert.Null(placement.Refusal);
        Assert.Equal(2, placement.Rows.Count);

        var bom = BomFile.Read("b.csv", BoardCompanions.BomTextOf(projection));
        Assert.Null(bom.Refusal);
        Assert.All(bom.Rows, r => Assert.Null(r.Value));
        Assert.All(bom.Rows, r => Assert.NotNull(r.Footprint));   // the cell each placement names

        output.WriteLine(projection.ThinnessSummary);
    }

    // ══ 8. Nothing is written on a refusal ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8, R-ab3-2e.</b> A piece of copper carrying two different net names: either the
    /// artwork shorts them or one label is wrong, and both readings are things a user must see. A
    /// half-written <c>.ipc</c> reads as a complete statement about a board and is one about part
    /// of it, so nothing is written at all.
    /// </summary>
    [Fact]
    public void CopperCarryingTwoNamesWritesNothing()
    {
        var fx = Board();

        // A second label on the SAME piece of copper — it overlaps the rail's own run, so the two
        // are one connected piece carrying two names.
        fx.View.Shapes.Add(new RectShape
        {
            Layer = Top, Net = "GND",
            X1 = Mm(5.5), Y1 = Mm(3.2), X2 = Mm(6.0), Y2 = Mm(3.3),
        });
        LayoutPersistence.SaveToFile(fx.Clay, fx.View);

        var projection = BoardCompanions.Project(fx.View, fx.Clay, fx.Tech);

        Assert.NotNull(projection.Refusal);
        Assert.Contains("two different net names", projection.Refusal!, StringComparison.Ordinal);
        Assert.Empty(projection.Pads);
        Assert.Throws<InvalidOperationException>(
            () => BoardCompanions.Write(projection, Path.Combine(_root, "never.ipc"), null, null));
        Assert.False(File.Exists(Path.Combine(_root, "never.ipc")));

        output.WriteLine(projection.Refusal!);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private sealed record Fx(LayoutView View, string Clay, Technology Tech);

    /// <summary>
    /// A two-part board: a rail run on TOP carrying both lands and a through via down to the plane,
    /// with the reference plane below. <paramref name="stamped"/> off is R-ab3-2d's thin case — the
    /// same geometry with nothing named on it.
    /// </summary>
    private Fx Board(bool stamped = true)
    {
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        string techPath = Path.Combine(_root, "tech", "Board.ctech");
        if (!File.Exists(techPath)) TechPersistence.SaveToFile(techPath, tech);
        if (!File.Exists(Path.Combine(_root, ".cws")))
            WorkspacePersistence.SaveToFile(
                Path.Combine(_root, ".cws"),
                new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        Land("Land");

        string cellDir = CellFolder.CreateCellFolder(_root, "Board" + (stamped ? "" : "Thin"));
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            TechRef = Path.Combine("..", "..", "tech", "Board.ctech"),
        };

        // The rail: one 0.2 mm TRACE reaching both parts' pin 1, and a barrel to the plane. NARROW
        // deliberately — the fast model refuses to price copper it reads as SPREADING rather than as
        // a trace, so a wide pour here would make the gate's own solve refuse for a reason that has
        // nothing to do with what it is measuring.
        view.Shapes.Add(new RectShape
        {
            Layer = Top, Net = stamped ? "VDD" : null,
            X1 = Mm(4.5), Y1 = Mm(3.15), X2 = Mm(8.7), Y2 = Mm(3.35),
        });
        view.Shapes.Add(new ViaShape
        {
            Layer = Barrel, LandingLayer = Top,
            X = Mm(6.5), Y = Mm(3.25), PadSize = Um(550), DrillSize = Um(300),
        });
        view.Shapes.Add(new RectShape { Layer = Mid, X1 = 0, Y1 = 0, X2 = Mm(14), Y2 = Mm(10) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(14), Y2 = Mm(10) });

        // Two parts. The LAND is what defeats symmetry — its two pins are off both axes and at
        // different offsets, so a swapped pad pair moves a coordinate and is not a mirror.
        view.Instances.Add(Place("C1", Mm(5), Mm(3), 0));
        view.Instances.Add(Place("C2", Mm(9), Mm(3), 0));

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return new Fx(view, clay, tech);
    }

    /// <summary>A land pattern: pins plus the copper under each, which is what makes it a land.</summary>
    private void Land(string name)
    {
        string cellDir = Path.Combine(_root, name);
        if (Directory.Exists(cellDir)) return;

        cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pinName, x, y) in LandPins)
        {
            cell.Pins.Add(new LayoutPin { Name = pinName, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            cell.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pinName,
                X1 = x - Um(125), Y1 = y - Um(125), X2 = x + Um(125), Y2 = y + Um(125),
            });
        }
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), cell);
    }

    private static LayoutInstance Place(string refdes, long x, long y, double rotation) => new()
    {
        CellRef = Path.Combine("..", "..", "Land"),
        X = x, Y = y, Mag = 1.0, RefDes = refdes, RotationDegrees = rotation,
    };

    private static RailDcRunResult Solve(
        Fx fx, IReadOnlyList<LayoutShape> shapes, RailArtwork.RailPadResolution pads)
    {
        var doc = new RailDocument { Name = "Board", ReferenceNet = "GND" };
        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "C1", Pin = "1" },
            OpenCircuitVoltageV = 3.3,
            SeriesResistanceOhms = 0.04,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "C2", Pin = "1" },
            DcCurrentA = 0.35,
        });
        doc.Rails.Add(rail);

        return RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Shapes = shapes,
            Technology = fx.Tech,
            DbuPerMicron = Dbu,
            Pads = pads.Pads,
            NetPoints = pads.NetPoints,
            ReferenceNet = "GND",
        });
    }

    /// <summary>Every cube's every value, in a comparable order — "the whole DataSet", literally.</summary>
    private static List<string> Flatten(RfCore.Data.DataSet data)
    {
        var rows = new List<string>();
        foreach (var (name, cube) in data.Cubes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (cube.DataKind == RfCore.Data.DataKind.Real)
                for (int i = 0; i < cube.RealValues.Length; i++)
                    rows.Add($"{name}[{i}]={cube.RealValues[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
            else
                for (int i = 0; i < cube.ComplexValues.Length; i++)
                    rows.Add($"{name}[{i}]={cube.ComplexValues[i]}");
        }
        return rows;
    }

    /// <param name="_">Three conductors, because a mounting loop needs a third to call the rail's
    /// plane — <c>LayoutPadsTests</c>' own note, and the reason the fixture's stackup is not two.</param>
    private static Technology TechFixture()
    {
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Mid, Name = "MID", ZOrder = 1, Color = new Rgba(90, 160, 90, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 2, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = Barrel, Name = "PTH", ZOrder = 3, Color = new Rgba(160, 160, 160, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MID",
                ThicknessDbu = Um(18), SigmaSm = 5.8e7, DrawingLayers = [Mid],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(1.2), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH",
                DrawingLayers = [Barrel], SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }
}
