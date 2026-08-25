using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Phase L4d's acceptance gates (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md §12).
///
/// <para>Fixtures live in <c>testdata/pcb-samples/</c> and are hand-authored for this phase rather than
/// taken from a real board. §10 asks for files this phase did not author and offers exactly this
/// fallback ("author a board in the originating tool and commit that instead") — the reason it is taken
/// here is that a redistributable real board could not be committed without also committing the
/// component and library names printed all over it, which root <c>CLAUDE.md</c> §"Commercial Vendor
/// References" forbids. <b>The dialect knowledge these fixtures encode is not invented</b>: every
/// spelling below was measured against four real boards spanning the 20171130, 20211014, 20221018 and
/// 20260206 epochs during §10's spike, and the measurements are recorded in
/// <c>src/Ui/RESOLVED.md</c>.</para>
/// </summary>
public class PcbImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pcb-import-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static string FixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "pcb-samples", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture not found: {name}");
    }

    private static PcbBoard ReadFixture(string name)
    {
        var read = PcbReader.Read(File.ReadAllText(FixturePath(name)), Dbu);
        Assert.Null(read.Refusal);
        return read.Board!;
    }

    private PcbImport.ImportResult ImportFixture(string name, Technology? destTech = null)
    {
        using var stream = File.OpenRead(FixturePath(name));
        return PcbImport.Import(stream, _dir, Path.GetFileNameWithoutExtension(name), destTech, Dbu);
    }

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir)) + ".clay";
        return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, name));
    }

    // ── Gate 2: handedness (R-L4d-3) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Y is DOWN in the source and UP in <c>.clay</c>. The fixture's outline is an L — asymmetric on
    /// BOTH axes, which is the WB-C lesson: a bridge between two coordinate conventions can only be
    /// tested by geometry that is off both axes, because anything symmetric in one of them imports
    /// identically whether the flip happened or not.
    /// </summary>
    [Fact]
    public void Gate2_LShapedOutline_ImportsAtTheCorrectHandedness()
    {
        var board = ReadFixture("handedness.kicad_pcb");
        var poly = Assert.IsType<PolygonShape>(board.Shapes.Select(s => s.Shape).OfType<PolygonShape>().Single());

        // Source (Y down):  (0,0) (10,0) (10,2) (2,2) (2,6) (0,6)
        // Imported (Y up):  X unchanged, Y negated. Written out by hand, not captured from the reader.
        long[] expected =
        [
            0, 0,
            10_000_000, 0,
            10_000_000, -2_000_000,
            2_000_000, -2_000_000,
            2_000_000, -6_000_000,
            0, -6_000_000,
        ];
        Assert.Equal(expected, poly.Xy);
    }

    /// <summary>The oracle can fail: a MIRRORED import (Y not flipped, or flipped twice) produces the
    /// source's own Y signs, and this fixture's assertion rejects it. Without this, gate 2 would pass on
    /// a reader that never flipped at all.</summary>
    [Fact]
    public void Gate2_AMirroredImport_WouldFailThatAssertion()
    {
        var poly = Assert.IsType<PolygonShape>(
            ReadFixture("handedness.kicad_pcb").Shapes.Select(s => s.Shape).OfType<PolygonShape>().Single());

        long[] mirrored = [0, 0, 10_000_000, 0, 10_000_000, 2_000_000, 2_000_000, 2_000_000, 2_000_000, 6_000_000, 0, 6_000_000];
        Assert.NotEqual(mirrored, poly.Xy);
    }

    // ── Gate 3: exact units (R-L4d-2) ───────────────────────────────────────────────────────────

    /// <summary>
    /// −12.3456 mm must land on exactly −12,345,600 DBU. <b>The negative case IS the test</b>: at the
    /// 1000 DBU/µm default, <c>(long)(-12.3456 * 1e6)</c> truncates toward zero to −12,345,599 while
    /// <c>Math.Round</c> gives −12,345,600, and the two agree on every positive coordinate — so a
    /// fixture drawn in the first quadrant cannot see the bug at all.
    /// </summary>
    [Fact]
    public void Gate3_NegativeMillimetres_LandOnExactDbu()
    {
        var board = ReadFixture("handedness.kicad_pcb");
        var track = board.Shapes.Select(s => s.Shape).OfType<PathShape>().Single(p => p.Xy[0] == -12_345_600);

        Assert.Equal(-12_345_600, track.Xy[0]);      // x: -12.3456 mm
        Assert.Equal(12_345_600, track.Xy[1]);       // y: -12.3456 mm, flipped up
        Assert.Equal(100, track.Xy[2]);              // x: 0.0001 mm = 100 nm
        Assert.Equal(-100, track.Xy[3]);
    }

    /// <summary>
    /// The arithmetic this reader must NOT do, stated so the gate's power is visible — and with one
    /// correction to R-L4d-2's own wording, found by writing the test.
    ///
    /// <para>The brief says a truncating cast "is therefore wrong only for negative coordinates". It is
    /// wrong on BOTH signs. The cast and the round differ exactly when the double product lands just
    /// SHORT of its integer, which is a property of the value's binary representation, not of its sign:
    /// −132.742022 mm multiplies to −132742021.99999999 and 66.383011 mm to 66383010.99999999, and the
    /// cast loses a nanometre on each. (−12.3456 × 1e6 happens to be exact, so the brief's own example
    /// value would not have caught it either way — which is why both are in the fixture.)</para>
    /// </summary>
    [Fact]
    public void Gate3_ATruncatingConversion_MissesByOneDbu_OnBothSigns()
    {
        Assert.Equal(-132_742_021, (long)(-132.742022 * 1e6));
        Assert.Equal(-132_742_022, PcbUnits.X(-132.742022, Dbu));

        Assert.Equal(66_383_010, (long)(66.383011 * 1e6));
        Assert.Equal(66_383_011, PcbUnits.X(66.383011, Dbu));
    }

    [Fact]
    public void Gate3_ThoseCoordinates_SurviveARealImport()
    {
        var board = ReadFixture("handedness.kicad_pcb");
        var track = board.Shapes.Select(s => s.Shape).OfType<PathShape>().Single(p => p.Xy[0] == -132_742_022);
        Assert.Equal(-66_383_011, track.Xy[1]);        // 66.383011 mm, flipped up
        Assert.Equal(-131_393_162, track.Xy[2]);
        Assert.Equal(64_361_741, track.Xy[3]);
    }

    // ── Gate 4: stackup present (R-L4d-5) ───────────────────────────────────────────────────────

    [Fact]
    public void Gate4_StackupPresent_MapsValuesAndKeepsTopToBottomOrder()
    {
        var result = ImportFixture("stackup-present.kicad_pcb");
        Assert.False(result.Cancelled);
        var stackup = Assert.IsType<Stackup>(result.Stackup);

        // Non-electrical entries (silk, mask) are skipped; what remains is Cu / core / Cu, in the file's
        // own order. A reversed stackup simulates cleanly and answers the wrong question.
        Assert.Equal(
            [StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor],
            stackup.Layers.Select(l => l.Kind));
        Assert.Equal(["F.Cu", "dielectric 1", "B.Cu"], stackup.Layers.Select(l => l.Name));

        Assert.Equal(35_000, stackup.Layers[0].ThicknessDbu);      // 0.035 mm
        Assert.Equal(1_510_000, stackup.Layers[1].ThicknessDbu);   // 1.51 mm
        Assert.Equal(70_000, stackup.Layers[2].ThicknessDbu);      // 0.07 mm — asymmetric on purpose,
                                                                   // so a swapped pair is detectable
        Assert.Equal(4.5, stackup.Layers[1].Epsr);
        Assert.Equal(0.02, stackup.Layers[1].TanD);
    }

    // ── Gate 5: stackup absent (R-L4d-6) ────────────────────────────────────────────────────────

    /// <summary>
    /// A board whose author never opened the stackup page has only an overall thickness. The geometry
    /// must import, the stackup must stay EMPTY, one message must say so — and <b>no substrate may be
    /// fabricated</b>, because an invented one is worse than none: nothing downstream will ever question
    /// it and it will be simulated.
    /// </summary>
    [Fact]
    public void Gate5_StackupAbsent_ImportsGeometry_LeavesStackupEmpty_AndSaysSo()
    {
        var result = ImportFixture("stackup-absent.kicad_pcb");
        Assert.False(result.Cancelled);

        Assert.Null(result.Stackup);                                  // nothing fabricated
        var view = LoadPrimaryLayout(result.BoardCellDir!);
        Assert.NotEmpty(view.Shapes);                                 // the geometry still came in

        var note = Assert.Single(result.Messages, m => m.Contains("no stackup section"));
        Assert.Contains("left EMPTY", note);
        Assert.Contains("no substrate was invented", note);
        Assert.Contains("1.55 mm", note);                             // the one substrate fact it DID carry
        Assert.Contains("relative permittivity", note);               // …and what the EM path still needs
    }

    // ── Gate 6: defaults reported (R-L4d-7, R-L4d-8) ────────────────────────────────────────────

    [Fact]
    public void Gate6_ConductivityAndMur_AreNamedAsDefaults_AndNothingIsInferredFromTheMaterial()
    {
        var result = ImportFixture("stackup-present.kicad_pcb");
        var stackup = result.Stackup!;

        Assert.Equal(PcbStackupMapping.DefaultCopperConductivitySm, stackup.Layers[0].SigmaSm);
        Assert.Equal(1.0, stackup.Layers[1].Mur);

        // R-L4d-8: one honest paragraph, not three silent assumptions.
        var note = Assert.Single(result.Messages, m => m.Contains("Not carried by this format"));
        Assert.Contains("conductivity", note);
        Assert.Contains("permeability", note);
        Assert.Contains("boundary conditions", note);
    }

    // ── Gate 7: unfilled stays unfilled (R-L4d-9) ───────────────────────────────────────────────

    /// <summary>The highest-consequence silent error in the phase: a <c>(fill no)</c> rect imported as a
    /// <see cref="RectShape"/> is an entire copper pour that does not exist on the board, and it will be
    /// meshed and simulated as one.</summary>
    [Fact]
    public void Gate7_UnfilledRectangle_IsAnOutline_NotACopperPour()
    {
        var board = ReadFixture("fills.kicad_pcb");
        var shapes = board.Shapes.Select(s => s.Shape).ToList();

        // Exactly ONE RectShape — the (fill yes) one, at x 11..19 mm.
        var rect = Assert.Single(shapes.OfType<RectShape>());
        Assert.Equal(11_000_000, rect.X1);
        Assert.Equal(19_000_000, rect.X2);

        // The (fill no) one is a stroked outline of four edges at the stroke width, closed.
        var outline = Assert.Single(shapes.OfType<PathShape>(), p => p.Xy.Length == 10);
        Assert.Equal(150_000, outline.Width);
        Assert.Equal(1_000_000, outline.Xy[0]);
        Assert.Equal(outline.Xy[0], outline.Xy[^2]);   // closed
        Assert.Equal(outline.Xy[1], outline.Xy[^1]);
    }

    // ── Gate 8: zones (R-L4d-11, R-L4d-12, R-L4d-13) ────────────────────────────────────────────

    [Fact]
    public void Gate8_FilledZoneImportsItsFill_UnfilledIsSkipped_KeepoutIsSkipped_OutlineIsNeverCopper()
    {
        var board = ReadFixture("fills.kicad_pcb");
        var polys = board.Shapes.Select(s => s.Shape).OfType<PolygonShape>().ToList();

        // ONE polygon from the zones: the GND zone's filled_polygon. The keepout's fill is not copper,
        // and the unfilled zone contributes nothing at all.
        var fill = Assert.Single(polys);
        Assert.Equal("GND", fill.Net);

        // The FILL is 4x4 mm; the zone OUTLINE is 40x20 mm. Importing the outline instead would be off
        // by a factor of fifty in area, which is what makes this an assertion rather than a shape count.
        Assert.Equal(4_000_000, fill.Xy.Where((_, i) => i % 2 == 0).Max() - fill.Xy.Where((_, i) => i % 2 == 0).Min());
        Assert.Equal(4_000_000, fill.Xy.Where((_, i) => i % 2 == 1).Max() - fill.Xy.Where((_, i) => i % 2 == 1).Min());

        var unfilled = Assert.Single(board.SkippedCounts, kv => kv.Key.Contains("unfilled zone"));
        Assert.Equal(1, unfilled.Value);
        Assert.Contains("SIG", unfilled.Key);          // by net, so the user knows which one to go fill

        var keepout = Assert.Single(board.SkippedCounts, kv => kv.Key.Contains("keepout"));
        Assert.Equal(1, keepout.Value);
    }

    // ── Gate 9: via orientation (R-L4d-10) ──────────────────────────────────────────────────────

    /// <summary>
    /// Barrel on <see cref="LayoutShape.Layer"/>, pad on <see cref="ViaShape.LandingLayer"/> — <b>proven
    /// by exporting and comparing, not by reading the two fields back</b> (§12 gate 9 says so in as many
    /// words). Reading the fields back only re-states whatever the reader wrote; the export is what
    /// actually puts copper somewhere, and getting this backwards "produces a GDSII/DXF export that looks
    /// plausible and puts copper where the hole should be" (<see cref="ViaShape"/>'s own doc comment).
    /// </summary>
    [Fact]
    public void Gate9_ExportedVia_PutsTheDrillOnTheBarrelLayerAndThePadOnTheLandingLayer()
    {
        var result = ImportFixture("via.kicad_pcb");
        var boardDir = result.BoardCellDir!;
        var view = LoadPrimaryLayout(boardDir);

        var via = view.Shapes.OfType<ViaShape>().First(v => v.DrillSize == 400_000);
        var barrelKey = via.Layer;
        var padKey = via.LandingLayer!.Value;
        Assert.NotEqual(barrelKey, padKey);        // two DIFFERENT layers, or the gate cannot be observed

        var plan = GdsiiExport.Analyze(boardDir, null, Dbu);
        Assert.True(plan.CanWrite);
        var outPath = Path.Combine(_dir, "via.gds");
        GdsiiExport.Write(outPath, plan);

        using var stream = File.OpenRead(outPath);
        var top = GdsiiReader.Open(stream).ReadStructures()
            .Single(s => s.Shapes.Count > 0);

        // Which layer got the DRILL-sized circle and which got the PAD-sized one — measured from the
        // exported geometry's own extent, never from the fields.
        long ExtentOn(LayerKey key) => top.Shapes
            .Where(s => s.Layer == key)
            .Select(LayoutGeometry.BboxOf)
            .Where(b => !b.IsEmpty)
            .Select(b => b.MaxX - b.MinX)
            .DefaultIfEmpty(0)
            .Max();

        long barrelExtent = ExtentOn(barrelKey);
        long padExtent = ExtentOn(padKey);

        // 0.4 mm drill vs 0.8 mm pad. A flattened circle is inscribed in its true diameter, so compare
        // by which is LARGER rather than against an exact number — the orientation is the claim, not
        // the flattening tolerance.
        Assert.True(barrelExtent > 0, "the barrel layer carries no geometry at all");
        Assert.True(padExtent > barrelExtent,
            $"the pad ({padExtent} DBU across) must be wider than the barrel ({barrelExtent}) — " +
            "if it is not, Layer and LandingLayer are the wrong way round");
    }

    [Fact]
    public void Gate9_BlindVia_IsReportedByCount_NamingWhereItWasPlaced()
    {
        var board = ReadFixture("via.kicad_pcb");
        var blind = Assert.Single(board.DegradedCounts, kv => kv.Key.Contains("blind/buried via"));
        Assert.Equal(1, blind.Value);
        Assert.Contains("F.Cu", blind.Key);            // names WHERE it was put, not merely that it was odd
    }

    // ── Gate 10: footprints (R-L4d-15) ──────────────────────────────────────────────────────────

    /// <summary>
    /// One cell per DEFINITION, N instances — including at a non-cardinal angle, which is the whole
    /// reason this phase depends on L3d.
    ///
    /// <para><b>The gate's literal "one cell and four instances" is not achievable for the back-side
    /// placement, and that is a measured fact rather than a shortfall.</b> A back-layer footprint's
    /// artwork is stored on the BACK-side layers (B.Cu, B.SilkS) with its local Y already negated — see
    /// <c>PcbReader.ReadFootprint</c> — so it is genuinely different artwork from its front-side twin.
    /// Sharing one cell between them would put front-side copper on the back of the board. What the gate
    /// is actually protecting — that the placement ANGLE does not multiply cells — is asserted in full:
    /// three placements at 0°, 90° and 37.5° share exactly one cell.</para>
    /// </summary>
    [Fact]
    public void Gate10_ThreeAnglesShareOneCell_AndTheBackSidePlacementIsItsOwn()
    {
        var board = ReadFixture("footprints.kicad_pcb");

        Assert.Equal(4, board.Placements.Count);
        Assert.Equal(2, board.FootprintCells.Count);

        var front = board.Placements.Where(p => p.RotationDegrees != 180).ToList();
        Assert.Equal(3, front.Count);
        Assert.Single(front.Select(p => p.ContentKey).Distinct());
        Assert.Equal([0.0, 90.0, 37.5], front.Select(p => p.RotationDegrees));

        var back = board.Placements.Single(p => p.RotationDegrees == 180);
        Assert.NotEqual(front[0].ContentKey, back.ContentKey);
    }

    /// <summary>
    /// Each instance renders identically to that footprint flattened in place. The expectation is
    /// computed HERE, from the placement angle and the pad's own corners, so it is an independent
    /// oracle rather than a re-run of <c>LayoutInstanceTransform</c>.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(37.5)]
    public void Gate10_AFlattenedInstance_MatchesTheHandComputedTransform(double degrees)
    {
        var result = ImportFixture("footprints.kicad_pcb");
        var boardDir = result.BoardCellDir!;
        var boardView = LoadPrimaryLayout(boardDir);
        var boardLayoutDir = CellFolder.SubFolderPath(boardDir, ViewType.Layout);

        var inst = boardView.Instances.Single(i => Math.Abs(i.RotationDegrees - degrees) < 1e-9);
        var flattened = LayoutFlatten.FlattenOneLevel(inst, boardLayoutDir)!.Shapes;

        // Pad "1" is a 1.2 x 0.8 mm rectangle centred at local (-1, 0) mm, i.e. (-1_000_000, 0) DBU.
        // Its four corners, rotated by the placement angle and offset by the placement — by hand.
        double rad = degrees * Math.PI / 180.0;
        double cos = degrees == 90 ? 0 : Math.Cos(rad);     // exact at the cardinal, as LayoutAngle is
        double sin = degrees == 90 ? 1 : Math.Sin(rad);
        var corners = new[] { (-600_000.0, -400_000.0), (600_000.0, -400_000.0), (600_000.0, 400_000.0), (-600_000.0, 400_000.0) }
            .Select(c => (X: c.Item1 - 1_000_000.0, Y: c.Item2))
            .Select(c => (X: inst.X + c.X * cos - c.Y * sin, Y: inst.Y + c.X * sin + c.Y * cos))
            .ToList();

        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X);
        double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);

        // Whatever carrier the flatten picked (a Rect at a cardinal angle, a Polygon at 37.5° — L3d's
        // own promotion rule), its extent must be the hand-computed one to within a DBU of rounding.
        var padBoxes = flattened
            .Select(LayoutGeometry.BboxOf)
            .Where(b => !b.IsEmpty)
            .ToList();
        Assert.Contains(padBoxes, b =>
            Math.Abs(b.MinX - minX) <= 1 && Math.Abs(b.MaxX - maxX) <= 1 &&
            Math.Abs(b.MinY - minY) <= 1 && Math.Abs(b.MaxY - maxY) <= 1);
    }

    // ── Gate 11: pads become pins (R-L4d-17) ────────────────────────────────────────────────────

    [Fact]
    public void Gate11_ATwoPadFootprint_YieldsTwoPinsWithNamesAndWidths()
    {
        var board = ReadFixture("footprints.kicad_pcb");
        var cell = board.FootprintCells.Values.First();

        Assert.Equal(2, cell.Pins.Count);
        Assert.Equal(["1", "2"], cell.Pins.Select(p => p.Pin.Name));
        // 1.2 x 0.8 mm pad: it faces along its LONG axis, so the width across that is 0.8 mm.
        Assert.All(cell.Pins, p => Assert.Equal(800_000, p.Pin.WidthDbu));
        Assert.All(cell.Pins, p => Assert.Equal("F.Cu", p.LayerName));
    }

    [Fact]
    public void Gate11_TheWrittenCell_CarriesThosePinsWithAResolvedLayer()
    {
        var result = ImportFixture("footprints.kicad_pcb");
        // Every non-board cell is a footprint cell; take the one that carries pins.
        var cellDir = result.CreatedCellDirs.First(d => d != result.BoardCellDir);
        var view = LoadPrimaryLayout(cellDir);

        Assert.Equal(2, view.Pins.Count);
        Assert.All(view.Pins, p => Assert.NotEqual(default, p.Layer));
        Assert.All(view.Pins, p => Assert.True(p.WidthDbu > 0));
    }

    // ── Gate 12: nets (R-L4d-18) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate12_ATracksNetIsTheName_AndNetZeroLeavesItNull()
    {
        var board = ReadFixture("nets.kicad_pcb");
        var tracks = board.Shapes.Select(s => s.Shape).OfType<PathShape>().OrderBy(p => p.Xy[0]).ToList();

        Assert.Equal(3, tracks.Count);
        Assert.Equal("VDD", tracks[0].Net);     // (net 7) -> the table's name, never "7"
        Assert.Null(tracks[1].Net);             // (net 0) -> the unassigned net: null, not ""
        Assert.Null(tracks[2].Net);             // no (net) at all
    }

    [Fact]
    public void Gate12_TheNameOnlySpelling_ResolvesWithNoOrdinalTableAtAll()
    {
        // The 20260206 epoch dropped the ordinal table entirely — measured on a real board.
        var board = ReadFixture("epoch-new.kicad_pcb");
        var track = board.Shapes.Select(s => s.Shape).OfType<PathShape>().Single(p => p.Net is not null);
        Assert.Equal("SIGNAL", track.Net);
    }

    // ── Gate 13: version tolerance (R-L4d-1) ────────────────────────────────────────────────────

    /// <summary>
    /// Two fixtures from different format epochs both import, neither is refused, and BOTH token-level
    /// differences are handled: the stroke spelling (<c>(width W)</c> vs <c>(stroke (width W) …)</c>)
    /// and the arc parameterisation (centre-plus-<c>(angle A)</c> vs three-point <c>(mid …)</c>). The
    /// two fixtures describe the same drawing; the assertion is that they produce the same geometry.
    /// </summary>
    [Fact]
    public void Gate13_TwoEpochsOfOneDrawing_ImportIdentically()
    {
        var older = ReadFixture("epoch-old.kicad_pcb");
        var newer = ReadFixture("epoch-new.kicad_pcb");

        Assert.Equal("20171130", older.Version);
        Assert.Equal("20260206", newer.Version);

        PathShape LineOf(PcbBoard b) => b.Shapes.Select(s => s.Shape).OfType<PathShape>()
            .Single(p => p.Edges is null && p.Xy[0] == 0 && p.Xy[1] == 0);
        Assert.Equal(LineOf(older).Width, LineOf(newer).Width);            // (width) vs (stroke (width))
        Assert.Equal(200_000, LineOf(older).Width);

        PathShape ArcOf(PcbBoard b) => b.Shapes.Select(s => s.Shape).OfType<PathShape>()
            .Single(p => p.Edges is [{ Kind: EdgeKind.Arc }]);
        var oldArc = ArcOf(older);
        var newArc = ArcOf(newer);

        // Both spell a quarter-circle of radius 2 mm centred at (10,10): from (12,10) to (10,12) in
        // source coordinates. Endpoints and bulge must agree to within a DBU / a rounding epsilon.
        Assert.Equal(oldArc.Xy[0], newArc.Xy[0]);
        Assert.Equal(oldArc.Xy[1], newArc.Xy[1]);
        Assert.True(Math.Abs(oldArc.Xy[2] - newArc.Xy[2]) <= 1);
        Assert.True(Math.Abs(oldArc.Xy[3] - newArc.Xy[3]) <= 1);
        Assert.Equal(oldArc.Edges![0].Bulge, newArc.Edges![0].Bulge, 6);
    }

    /// <summary>The centre-plus-angle spelling is not a chord: <c>(start)</c> is the CENTRE. A reader
    /// that treats it as the three-point layout draws a straight line through the middle of every
    /// rounded silkscreen, silently — so the arc's own geometry is asserted, not merely its presence.</summary>
    [Fact]
    public void Gate13_TheOldArcSpelling_IsCentrePlusSweep_NotAChord()
    {
        var older = ReadFixture("epoch-old.kicad_pcb");
        var arc = older.Shapes.Select(s => s.Shape).OfType<PathShape>().Single(p => p.Edges is [{ Kind: EdgeKind.Arc }]);

        // (gr_arc (start 10 10) (end 12 10) (angle 90)) — centre (10,10), first point (12,10),
        // sweeping 90 degrees COUNTER-clockwise in the source's Y-down frame, which is CLOCKWISE once
        // Y is flipped up. So the arc runs (12,-10) -> (10,-12) in DBU, and the bulge is tan(-90/4).
        Assert.Equal(12_000_000, arc.Xy[0]);
        Assert.Equal(-10_000_000, arc.Xy[1]);
        Assert.Equal(10_000_000, arc.Xy[2]);
        Assert.Equal(-12_000_000, arc.Xy[3]);
        Assert.Equal(-Math.Tan(Math.PI / 8), arc.Edges![0].Bulge, 9);
    }

    /// <summary>A file whose layer table names its copper <c>top_layer</c>/<c>bottom_layer</c> — no
    /// ".Cu" anywhere — still expands a pad's <c>*.Cu</c> wildcard, because the expansion runs on the
    /// table's TYPE word. Measured on a real 20171130-epoch board, where the user's own layer name had
    /// replaced the canonical one outright.</summary>
    [Fact]
    public void Gate13_WildcardCopperExpansion_UsesTheTablesTypeWord_NotANameSuffix()
    {
        var older = ReadFixture("epoch-old.kicad_pcb");
        var cell = Assert.Single(older.FootprintCells.Values);

        // *.Cu over a table whose copper is named top_layer / bottom_layer -> both, and nothing else.
        // (The pad is drilled, so its copper is an annulus — a polygon with a hole, not a disc.)
        var padLayers = cell.Shapes.Where(s => s.Shape is not ViaShape).Select(s => s.LayerName).ToList();
        Assert.Equal(["top_layer", "bottom_layer"], padLayers);
    }

    // ── Gate 14: unknown tokens ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate14_AnUnrecognizedToken_IsReportedOnceWithACount_AndEverythingElseImports()
    {
        var result = ImportFixture("unknown-token.kicad_pcb");
        Assert.False(result.Cancelled);

        var view = LoadPrimaryLayout(result.BoardCellDir!);
        Assert.Equal(2, view.Shapes.OfType<PathShape>().Count());       // both real lines still came in

        var reports = result.Messages.Where(m => m.Contains("flux_capacitor")).ToList();
        var report = Assert.Single(reports);                            // ONCE, not per occurrence
        Assert.Contains("3 occurrence", report);
    }

    // ── Gate 15: the ceiling (R-L4d-20) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse BEFORE allocating, naming the number. A reader that dies partway through a large board
    /// leaves the user with a half-imported layout and no explanation — which is why this is a
    /// first-pass count over the raw text rather than a check inside the loop.
    /// </summary>
    [Fact]
    public void Gate15_AnOversizedBoard_IsRefusedBeforeAnythingIsCreated_WithItsNumberNamed()
    {
        long over = PcbReader.EntityHardCeiling + 10;
        var text = new System.Text.StringBuilder("(kicad_pcb (version 20260206) (layers (0 \"F.Cu\" signal))");
        for (long i = 0; i < over; i++)
            text.Append("(segment (start 0 0) (end 1 1) (width 0.2) (layer \"F.Cu\") (net 0))");
        text.Append(')');

        var path = Path.Combine(_dir, "huge.kicad_pcb");
        File.WriteAllText(path, text.ToString());

        using var stream = File.OpenRead(path);
        var result = PcbImport.Import(stream, _dir, "huge", null, Dbu);

        Assert.True(result.Cancelled);
        Assert.Empty(result.CreatedCellDirs);
        Assert.Null(result.BoardCellDir);
        var refusal = Assert.Single(result.Messages);
        Assert.Contains(PcbReader.EntityHardCeiling.ToString("N0"), refusal);
        Assert.Contains(over.ToString("N0"), refusal);
    }

    [Fact]
    public void Gate15_ABoardJustUnderTheCeiling_IsNotRefused()
    {
        // The oracle can fail: without this, a reader that refused EVERYTHING would pass gate 15.
        var text = "(kicad_pcb (version 20260206) (layers (0 \"F.Cu\" signal))" +
                   "(segment (start 0 0) (end 1 1) (width 0.2) (layer \"F.Cu\") (net 0)))";
        var read = PcbReader.Read(text, Dbu);
        Assert.Null(read.Refusal);
        Assert.Equal(1, read.Board!.EntitiesRead);
    }

    // ── Gate 16: counters, never wall clock (R-L4d-21) ──────────────────────────────────────────

    /// <summary>
    /// Entities read, shapes produced, cells created. <b>No wall-clock assertion anywhere in this
    /// file</b> — root <c>CLAUDE.md</c>'s benchmark rule and the standing "no new timing tests"
    /// instruction both apply, and a timing assertion here would measure the machine.
    /// </summary>
    [Fact]
    public void Gate16_CountersAreAsserted()
    {
        var board = ReadFixture("footprints.kicad_pcb");

        // 4 footprints + (2 fp_line + 2 pad) x 4 = 20 entities.
        Assert.Equal(20, board.EntitiesRead);
        // Each cell holds 2 silk lines + 2 pads = 4 shapes; 4 placements render 16.
        Assert.Equal(16, board.ShapesProduced);
        Assert.Equal(2, board.FootprintCells.Count);

        var result = ImportFixture("footprints.kicad_pcb");
        Assert.Equal(3, result.CreatedCellDirs.Count);            // 2 footprint cells + the board
        var summary = Assert.Single(result.Messages, m => m.StartsWith("Imported "));
        Assert.Contains("20 entities", summary);
    }

    // ── Scale (R-L4d-19) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWholeBoardIsImported_AndTheSummarySaysWhatComesNext()
    {
        var result = ImportFixture("fills.kicad_pcb");
        var note = Assert.Single(result.Messages, m => m.Contains("The whole board was imported"));
        Assert.Contains("crop", note);
    }

    // ── Layer aliasing (R-L4d-4) ────────────────────────────────────────────────────────────────

    [Fact]
    public void ADestinationLayerDeclaringAPcbLayerNameAlias_ClaimsTheSourceLayerDirectly()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        tech.Layers[0].Interchange = new InterchangeMapping(null, null, null, null, null, "F.Cu");

        var result = ImportFixture("stackup-present.kicad_pcb", tech);
        var view = LoadPrimaryLayout(result.BoardCellDir!);

        // The track drawn on F.Cu landed on the destination's own Top Copper key, with no dialog and no
        // new layer added for it.
        Assert.Contains(view.Shapes, s => s.Layer == tech.Layers[0].Key);
        Assert.DoesNotContain(result.LayersToAdd, l => l.Name == "F.Cu");
    }

    // ── Pads (R-L4d-16) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPadShape_BecomesTheCarrierTheTableNames()
    {
        var cell = Assert.Single(ReadFixture("pads.kicad_pcb").FootprintCells.Values);
        var byPin = cell.Shapes.Select(s => s.Shape).ToList();

        Assert.Single(byPin.OfType<CircleShape>(), c => c.R == 500_000);                  // circle
        Assert.Contains(byPin.OfType<RectShape>(), r => r.X2 - r.X1 == 1_200_000);        // rect
        Assert.Contains(byPin.OfType<PathShape>(),                                        // oval
            p => p.End == PathEndStyle.Round && p.Width == 1_000_000 && p.Xy.Length == 4);
        // roundrect: CornerRadius = roundrect_rratio x min(size.x, size.y) = 0.25 x 1.0 mm
        Assert.Contains(byPin.OfType<RoundedRectShape>(), r => r.CornerRadius == 250_000);
        // trapezoid and custom both land as polygons; the custom one is its own (primitives …),
        // read through the SAME graphics path as §5 rather than a second reader.
        Assert.True(byPin.OfType<PolygonShape>().Count() >= 2);
    }

    /// <summary>What a <see cref="ViaShape"/> cannot carry is reported by count, saying what was done
    /// instead — never silently rounded with nothing said (R-L4d-16).</summary>
    [Fact]
    public void OvalDrillsAndDrillOffsets_AreReportedByCount_NotSilentlyRounded()
    {
        var board = ReadFixture("pads.kicad_pcb");

        var oval = Assert.Single(board.DegradedCounts, kv => kv.Key.Contains("oval pad drill"));
        Assert.Equal(1, oval.Value);
        Assert.Contains("slot", oval.Key);

        // A drill OFFSET is no longer among them: it moves the pad's copper, which circuitRF carries
        // exactly, because the hole and the copper are separate shapes here.
        Assert.DoesNotContain(board.DegradedCounts, kv => kv.Key.Contains("drill offset"));
    }

    /// <summary>
    /// "Not imported" and "imported, but degraded" are reported under different headings, because one
    /// sentence covering both says something false about half of them. An outline whose fill flag the
    /// file never stated IS in the layout; a keepout zone is not.
    /// </summary>
    [Fact]
    public void DegradedImportsAreNotReportedAsSkippedOnes()
    {
        var result = ImportFixture("pads.kicad_pcb");
        var degraded = Assert.Single(result.Messages, m => m.Contains("oval pad drill"));
        Assert.DoesNotContain("not imported", degraded);

        var skipped = Assert.Single(ImportFixture("fills.kicad_pcb").Messages, m => m.Contains("keepout"));
        Assert.Contains("not imported", skipped);
    }

    [Fact]
    public void EveryExistingCtech_StillRoundTrips_WithTheAdditivePcbLayerNameField()
    {
        // R-L4d-4: additive and nullable, exactly like DxfLayerName — a mapping that sets nothing else
        // still normalizes away to a null Interchange, so no .ctech FormatVersion bump is implied.
        var m = new InterchangeMapping(null, null, null, null, null);
        Assert.Null(m.PcbLayerName);
        Assert.Equal(m, new InterchangeMapping(null, null, null, null, null, null));
    }

    // ── Text anchoring (owner report, 2026-08-25) ───────────────────────────────────────────────

    /// <summary>
    /// <c>(justify …)</c> reaches <see cref="LabelShape.HAlign"/>/<see cref="LabelShape.VAlign"/>, and
    /// — the half that actually caused the bug — <b>an unstated justification is CENTRED, not
    /// left-of-baseline.</b> This format's default anchor is the centre of the text box on both axes;
    /// circuitRF's is the left end of the baseline. A reader that leaves the fields null therefore
    /// silently displaces every string by half its own width, and every <c>left top</c> one (which is
    /// what a generated stackup table is made of) by a full cap height.
    /// </summary>
    [Fact]
    public void TextJustification_IsRead_AndAnUnstatedOneMeansCentred()
    {
        var view = LoadPrimaryLayout(ImportFixture("text-justify.kicad_pcb").BoardCellDir!);
        LabelShape Label(string text) => view.Shapes.OfType<LabelShape>().Single(l => l.Text == text);

        var unstated = Label("unstated");
        Assert.Equal(LabelHAlign.Center, unstated.HAlign);
        Assert.Equal(LabelVAlign.Middle, unstated.VAlign);

        Assert.Equal(LabelHAlign.Left,  Label("left top").HAlign);
        Assert.Equal(LabelVAlign.Top,   Label("left top").VAlign);

        Assert.Equal(LabelHAlign.Right,  Label("right bottom").HAlign);
        Assert.Equal(LabelVAlign.Bottom, Label("right bottom").VAlign);

        // One axis stated, the other not — the unstated axis keeps the format's own default rather
        // than inheriting the stated one.
        Assert.Equal(LabelHAlign.Left,   Label("left only").HAlign);
        Assert.Equal(LabelVAlign.Middle, Label("left only").VAlign);
    }

    /// <summary>
    /// Mirrored text is a back-side rendering flag with no counterpart in layout. The glyphs come in
    /// forwards — said so, per R-L4d-1 — but <b>the anchor still swaps sides</b>: mirroring reverses the
    /// text's own x axis, so the end of the string sitting at the anchor is the other one. Owner report,
    /// 2026-08-25: without the swap a mirrored left-justified annotation renders on the wrong side of
    /// its own anchor, which is a placement error, not a legibility one.
    /// </summary>
    [Fact]
    public void MirroredText_ImportsUnmirrored_ButKeepsTheAnchorOnTheCorrectSide()
    {
        var board = ReadFixture("text-justify.kicad_pcb");
        var mirrored = Assert.Single(board.DegradedCounts, kv => kv.Key.Contains("mirrored text"));
        Assert.Equal(3, mirrored.Value);

        var view = LoadPrimaryLayout(ImportFixture("text-justify.kicad_pcb").BoardCellDir!);
        LabelShape Label(string text) => view.Shapes.OfType<LabelShape>().Single(l => l.Text == text);

        Assert.Equal(LabelHAlign.Right, Label("mirrored left").HAlign);
        Assert.Equal(LabelHAlign.Left,  Label("mirrored right").HAlign);
        Assert.Equal(LabelHAlign.Center, Label("mirrored plain").HAlign);   // centre is its own mirror

        // The vertical half is untouched — a board flip is an X mirror.
        Assert.Equal(LabelVAlign.Middle, Label("mirrored left").VAlign);
    }

    /// <summary>A board's text angle is arbitrary and must arrive that way. It used to snap to the
    /// nearest 90 degrees, which leaves an annotation plausibly drawn and visibly in the wrong
    /// place — owner report, 2026-08-25.</summary>
    [Fact]
    public void TextAtANonCardinalAngle_IsCarriedExactly_NotSnapped()
    {
        var result = ImportFixture("text-justify.kicad_pcb");
        var view = LoadPrimaryLayout(result.BoardCellDir!);
        var angled = view.Shapes.OfType<LabelShape>().Single(l => l.Text == "angled");

        Assert.Equal(45.0, angled.RotationDegrees, 6);
        Assert.DoesNotContain(result.Messages, m => m.Contains("snapped", StringComparison.OrdinalIgnoreCase));
    }

    // ── Through-hole pads (owner report, 2026-08-25) ────────────────────────────────────────────

    /// <summary>
    /// <b>A footprint pad's drill contributes the HOLE, and nothing else.</b> The pad's own copper
    /// landing is already in the cell — the reader emits its real outline once per copper layer the pad
    /// occupies — so a <see cref="ViaShape"/> that also claimed a <see cref="ViaShape.LandingLayer"/>
    /// and a pad-sized radius would put a SECOND, round, oversized copper pad on top of the first.
    ///
    /// <para>Measured on a real board before the fix: a 5 x 10 mm rectangular pad came in with a 10 mm
    /// copper disc centred on it, overhanging by 2.5 mm on every side — visible in the editor, and real
    /// copper to <c>DrcRegions</c>, <c>GdsiiWriter</c> and <c>DxfWriter</c>, all three of which read
    /// PadSize-on-LandingLayer as a filled pad.</para>
    /// </summary>
    [Fact]
    public void AFootprintPadsDrill_ContributesOnlyTheBarrel_NeverASecondCopperPad()
    {
        var result = ImportFixture("pads.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => d != result.BoardCellDir);
        var view = LoadPrimaryLayout(cellDir);

        // Fixture pad "8": thru_hole circle, 1.6 mm pad, 0.8 mm drill, on *.Cu.
        var via = view.Shapes.OfType<ViaShape>().Single(v => v.DrillSize == 800_000);
        Assert.Null(via.LandingLayer);
        Assert.Equal(via.DrillSize, via.PadSize);

        // …and the copper landing is still there, as the pad's own artwork, on BOTH copper layers —
        // as an ANNULUS, because the hole really is drilled through it.
        var pads = view.Shapes.Where(sh => sh is not ViaShape)
                              .Where(sh => LayoutGeometry.BboxOf(sh).Contains(14_000_000, 0)
                                        && LayoutGeometry.BboxOf(sh).MaxX - LayoutGeometry.BboxOf(sh).MinX <= 1_610_000)
                              .ToList();
        Assert.Equal(2, pads.Count);
        Assert.Equal(2, pads.Select(p => p.Layer).Distinct().Count());
        Assert.All(pads, p => Assert.Equal(2, LayoutFlattener.Flatten(p, 200).Count));   // outer ring + the hole
    }

    // ── Layer aliasing (R-L4d-4) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A board's copper lands on the destination technology's OWN copper when that technology declares
    /// the alias — which the shipped PCB starters now do. Without it every board layer mints a synthetic
    /// key, and an import into a stock PCB workspace silently doubles the layer table: a second copper
    /// layer beside Top Copper, with the board's tracks on the new one and the technology's stackup,
    /// DRC rules and EM extraction all still pointing at the old.
    /// </summary>
    [Fact]
    public void ImportingIntoTheShippedPcbTechnology_LandsCopperOnItsOwnCopperLayers()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var topCopper = tech.Layers.Single(l => l.Name == "Top Copper").Key;

        var result = ImportFixture("pads.kicad_pcb", tech);
        var cellDir = Assert.Single(result.CreatedCellDirs, d => d != result.BoardCellDir);
        var view = LoadPrimaryLayout(cellDir);

        Assert.Contains(view.Shapes, s => s.Layer == topCopper);
        Assert.DoesNotContain(result.LayersToAdd, d => d.Name is "F.Cu" or "B.Cu");
    }

    // ── Custom pads and no-copper pads (owner report, 2026-08-25) ───────────────────────────────

    /// <summary>
    /// <b>A custom pad is its anchor shape UNION every one of its primitives.</b> Importing only the
    /// first primitive left a lone stroke or arc hanging past the courtyard where the file had a
    /// multi-piece pad — geometry that looks like a bug rather than like the drop it was — and dropping
    /// the anchor removed the pad's own body, so a custom pad frequently arrived with no copper at all
    /// under the pin that names it.
    /// </summary>
    [Fact]
    public void ACustomPad_ImportsItsAnchorAndEveryPrimitive_NotJustTheFirst()
    {
        var result = ImportFixture("pads-no-copper-and-custom.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains("CUSTOM", StringComparison.Ordinal)
                                                                && !Path.GetFileName(d).Contains("RECT", StringComparison.Ordinal));
        var shapes = LoadPrimaryLayout(cellDir).Shapes;

        // The anchor disc, the stroked line and the filled rectangle all touch, so they union into ONE
        // region; the filled circle at x = 8 mm touches nothing and stays its own piece.
        Assert.Equal(2, shapes.Count);
        Assert.All(shapes, sh => Assert.IsType<PolygonShape>(sh));

        // …and the whole pad is there, out to the last primitive at x = 9 mm — before this, only the
        // FIRST primitive was imported and everything past x = 4 mm was missing.
        var bbox = shapes.Select(LayoutGeometry.BboxOf).Aggregate(Bbox.Empty, (a, b) => a.Union(b));
        Assert.Equal(9_000_000, bbox.MaxX);
        // The anchor disc, radius 1 mm about the origin. Within a micron: a union is polygonal, so a
        // circular boundary lands just inside its true extreme.
        Assert.InRange(bbox.MinX, -1_000_000, -998_000);
    }

    /// <summary><c>(anchor rect)</c> names a rectangle of the pad's own <c>(size …)</c>, not a circle —
    /// the difference is a pad body of the wrong shape, which nothing downstream would question.</summary>
    [Fact]
    public void ACustomPadsRectAnchor_IsARectangleOfThePadsOwnSize()
    {
        var result = ImportFixture("pads-no-copper-and-custom.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains("RECT_ANCHOR", StringComparison.Ordinal));
        var shapes = LoadPrimaryLayout(cellDir).Shapes;

        // A 3 x 1 mm anchor about the origin, and a primitive out at x = 2..4 mm that does not touch
        // it — so two pieces, and the anchor's own extent is the pad's (size …), not a circle of it.
        Assert.Equal(2, shapes.Count);
        var anchor = shapes.Select(LayoutGeometry.BboxOf).Single(b => b.MinX == -1_500_000);
        Assert.Equal(3_000_000, anchor.MaxX - anchor.MinX);
        Assert.Equal(1_000_000, anchor.MaxY - anchor.MinY);
    }

    /// <summary>
    /// <b>A pad with no copper on any layer IS its aperture.</b> The margin argument that keeps a
    /// copper pad's mask opening from being invented does not apply when there is no copper to expand
    /// from — and dropping it produced a cell holding nothing but a courtyard rectangle, which is what
    /// the owner saw ("the ChamfnRRect and Circ cells are empty").
    /// </summary>
    [Theory]
    [InlineData("MASK_ONLY", 2)]     // *.Mask expands to BOTH mask layers, so the opening lands on each
    [InlineData("PASTE_ONLY", 1)]
    public void APadWithNoCopperAnywhere_ImportsAsItsOwnAperture(string cellName, int expectedApertures)
    {
        var result = ImportFixture("pads-no-copper-and-custom.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains(cellName, StringComparison.Ordinal));
        var view = LoadPrimaryLayout(cellDir);

        var apertures = view.Shapes.Where(sh => sh is not ViaShape).ToList();
        Assert.Equal(expectedApertures, apertures.Count);
        Assert.All(apertures, sh => Assert.DoesNotContain(view.Pins, p => p.Layer == sh.Layer));

        Assert.Contains(result.Messages, m => m.Contains("pad with no copper on any layer"));

        // It is NOT copper: no pin is minted, because there is nothing there to connect to.
        Assert.Empty(view.Pins);
    }

    /// <summary>A pad that HAS copper keeps the old behaviour exactly — its mask/paste apertures are
    /// still not generated, because there the aperture really is the copper plus a margin this format
    /// does not state on the pad.</summary>
    [Fact]
    public void APadWithCopper_StillDoesNotGenerateItsMaskAperture()
    {
        var result = ImportFixture("pads-no-copper-and-custom.kicad_pcb");
        Assert.Contains(result.Messages, m => m.Contains("pad aperture on a non-copper layer"));

        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains("RECT_ANCHOR", StringComparison.Ordinal));
        var view = LoadPrimaryLayout(cellDir);
        Assert.Single(view.Pins);
        Assert.All(view.Shapes, sh => Assert.Equal(view.Pins[0].Layer, sh.Layer));
    }

    // ── Pad outlines, against the originating tool's own plot (owner report, 2026-08-25) ────────
    //
    // The owner installed the originating tool and compared its rendering of a real board with
    // circuitRF's. The expected coordinates below are TRANSCRIBED from Gerber that tool plotted from
    // that same board — its aperture coordinates are pad-local and Y-UP, which is circuitRF's own cell
    // convention, so they are directly comparable and are quoted verbatim rather than re-derived.

    /// <summary>
    /// <b>A trapezoid tapers; it does not shear.</b> The sign on the near pair of corners was inverted,
    /// which slopes both ends of the pad the SAME way — a parallelogram with the same area and the same
    /// bounding box on one axis, so only the shape itself shows it ("the trapezoid renderings look
    /// different"). Both deltas are pinned separately because a fixture exercising only one of them
    /// cannot tell the taper axis from the shear.
    ///
    /// <para>Note that a trapezoid pad legitimately reaches BEYOND its own <c>(size …)</c> on one side —
    /// the delta lengthens one edge and shortens the other — so "the pad is outside its box" is the
    /// feature, not a fault in whichever tool drew it.</para>
    /// </summary>
    [Theory]
    // DX tapers along Y, DY along X — crossed, which is the format's own convention.
    [InlineData("TRAP_X", new long[] { -2_500_000, -5_317_500, 2_500_000, -4_682_500, 2_500_000, 4_682_500, -2_500_000, 5_317_500 })]
    [InlineData("TRAP_Y", new long[] { -2_182_500, -5_000_000, 2_182_500, -5_000_000, 2_817_500, 5_000_000, -2_817_500, 5_000_000 })]
    public void ATrapezoidPad_TapersOnTheAxisItsDeltaNames(string cellName, long[] expected)
    {
        var poly = Assert.IsType<PolygonShape>(Assert.Single(PadShapesOf(cellName)));
        Assert.Equal(expected, poly.Xy);
    }

    /// <summary>A chamfer CUTS its corner. The model's rounded rectangle rounds all four equally and is
    /// axis-aligned by type, so the general boundary is built instead — and the corner names resolve in
    /// the layout's own Y-up frame, which is where this could silently have chamfered the wrong two
    /// corners and still looked like a chamfered pad.</summary>
    [Fact]
    public void AChamferedPad_CutsExactlyTheCornersItNames()
    {
        var curve = Assert.IsType<CurveShape>(Assert.Single(PadShapesOf("CHAMF_RECT")));

        // chamfer_ratio 0.2 x min(5, 10) = 1 mm, on three of the four corners. Every edge is straight
        // (rratio 0), so there are 7 vertices — one for the untouched top-left corner, two for each cut.
        Assert.Equal(7, curve.Xy.Length / 2);
        Assert.All(curve.Edges!, e => Assert.Equal(EdgeKind.Line, e.Kind));

        var pts = curve.Xy.Chunk(2).Select(c => (X: c[0], Y: c[1])).ToHashSet();
        Assert.Contains((-2_500_000L, 5_000_000L), pts);   // top_left: NOT named, so still a sharp corner
        Assert.DoesNotContain((2_500_000L, 5_000_000L), pts);    // top_right
        Assert.DoesNotContain((-2_500_000L, -5_000_000L), pts);  // bottom_left
        Assert.DoesNotContain((2_500_000L, -5_000_000L), pts);   // bottom_right
        foreach (var p in new[] { (1_500_000L, 5_000_000L), (2_500_000L, 4_000_000L),
                                  (-1_500_000L, -5_000_000L), (-2_500_000L, -4_000_000L),
                                  (2_500_000L, -4_000_000L), (1_500_000L, -5_000_000L) })
            Assert.Contains(p, pts);
    }

    /// <summary>A rounded rectangle at a non-cardinal angle used to lose its corner radius entirely —
    /// it became a plain rotated rectangle, which is a bigger pad than the file describes. It now keeps
    /// the radius as quarter-circle arc edges on the general boundary.</summary>
    [Fact]
    public void ARoundedRectPadAtANonCardinalAngle_KeepsItsCornerRadius()
    {
        var curve = Assert.IsType<CurveShape>(Assert.Single(PadShapesOf("ROUNDRECT_ROT")));
        Assert.Equal(8, curve.Xy.Length / 2);
        Assert.Equal(4, curve.Edges!.Count(e => e.Kind == EdgeKind.Arc));

        // rratio 0.25 x min(5, 10) = 1.25 mm radius, so 4 corners lose (2 - pi/2) r^2 from 5 x 10.
        double expected = 50.0 - (4 - Math.PI) * 1.25 * 1.25;
        Assert.Equal(expected, AreaMm2(curve), 2);
    }

    /// <summary>
    /// <b>A custom pad is ONE region, and a filled primitive's own <c>(width …)</c> is part of it.</b>
    /// The originating tool plots a custom pad as a single aperture outline with no internal edges, and
    /// draws each filled primitive as fill PLUS a pen stroke of that width — so the copper reaches half
    /// a width past the outline. Handing the pieces through separately drew every internal edge (owner
    /// report: "renders as some strange boolean"), and dropping the pen left the pad short.
    /// </summary>
    [Fact]
    public void ACustomPad_IsOneUnionedRegion_IncludingEachFilledPrimitivesPen()
    {
        var poly = Assert.IsType<PolygonShape>(Assert.Single(PadShapesOf("CUSTOM_FILLED_WIDTH")));
        var bbox = LayoutGeometry.BboxOf(poly);

        // One region: a 4 x 4 right triangle from the origin, its own 0.2 mm pen, and a 1 mm anchor
        // disc about the origin — all touching. The bbox is the whole assertion, because each of the
        // three contributes exactly one of its four sides:
        //   +x, and -y after the Y flip:  the triangle's far corner, plus half a pen  = 4.1 mm
        //   -x, and +y after the Y flip:  the anchor disc's own radius                = 0.5 mm
        // Within a micron on each: a union is polygonal, so a rounded corner or a disc lands just
        // inside its true extreme.
        Assert.InRange(bbox.MaxX, 4_098_000, 4_100_000);
        Assert.InRange(bbox.MinY, -4_100_000, -4_098_000);
        Assert.InRange(bbox.MinX, -500_000, -498_000);
        Assert.InRange(bbox.MaxY, 498_000, 500_000);

        // Dropping the pen was worth 0.1 mm of copper on every bounding side, and dropping the union
        // left the pieces separately outlined — this is both, in one number.
        Assert.True(AreaMm2(poly) > 8.0, $"the pad must exceed the bare 8 mm2 triangle, got {AreaMm2(poly):F4}");
    }

    /// <summary>
    /// <c>(drill … (offset …))</c> moves the pad's COPPER, not its hole: the pad's own <c>(at …)</c> is
    /// where the hole goes, and the shape sits at <c>at + offset</c>, turned by the pad's orientation.
    /// It used to be reported as "not expressible", which had it backwards — circuitRF carries the hole
    /// and the copper as separate shapes, so an offset hole is the natural representation. What was
    /// wrong was putting the copper on the hole.
    /// </summary>
    [Fact]
    public void ADrillOffset_MovesTheCopper_AndLeavesTheHoleOnThePadsOwnPosition()
    {
        var result = ImportFixture("pads-trapezoid-chamfer.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains("DRILL_OFFSET", StringComparison.Ordinal));
        var shapes = LoadPrimaryLayout(cellDir).Shapes;

        var via = Assert.Single(shapes.OfType<ViaShape>());
        Assert.Equal(0, via.X);
        Assert.Equal(0, via.Y);

        // (offset 0 1.905), and Y is negated on import, so the copper sits 1.905 mm BELOW the hole.
        foreach (var copper in shapes.Where(sh => sh is not ViaShape))
        {
            var bbox = LayoutGeometry.BboxOf(copper);
            Assert.Equal(-1_905_000, (bbox.MinY + bbox.MaxY) / 2);
            Assert.Equal(0, (bbox.MinX + bbox.MaxX) / 2);
        }

        Assert.DoesNotContain(result.Messages, m => m.Contains("drill offset"));
    }

    /// <summary>
    /// <b>An oval drill is a SLOT.</b> The reader used to take the larger of the two diameters and draw
    /// one round hole of it — the wrong shape, and too wide ACROSS the slot, which is the dimension a
    /// fab reads. Owner report, 2026-08-25, comparing renderings: "[the originating tool] renders an elongated hole, but
    /// circuitRF renders a simple circle."
    /// </summary>
    [Fact]
    public void AnOvalDrill_IsDrawnAsTheSlotItIs_NotAsACircleOfItsLength()
    {
        // Fixture pad "7": thru_hole oval, 2.0 x 1.0 pad, (drill oval 1.2 0.6) at x = 12 mm.
        var result = ImportFixture("pads.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => d != result.BoardCellDir);
        var shapes = LoadPrimaryLayout(cellDir).Shapes;

        var slot = Assert.Single(shapes.OfType<PathShape>().Where(p => p.Width == 600_000));
        Assert.Equal(PathEndStyle.Round, slot.End);
        Assert.Equal(600_000, slot.Width);                       // across: the narrow diameter
        Assert.Equal(600_000, slot.Xy[2] - slot.Xy[0]);          // along: 1.2 - 0.6, plus the round caps
        Assert.Equal(12_000_000, (slot.Xy[0] + slot.Xy[2]) / 2); // centred on the pad

        // …and the barrel it belongs to is the slot's WIDTH, not its length — the narrow dimension is
        // what the hole measures everywhere along it.
        var via = Assert.Single(shapes.OfType<ViaShape>().Where(v => v.X == 12_000_000));
        Assert.Equal(600_000, via.DrillSize);
    }

    private IReadOnlyList<LayoutShape> PadShapesOf(string cellName)
    {
        var result = ImportFixture("pads-trapezoid-chamfer.kicad_pcb");
        var cellDir = Assert.Single(result.CreatedCellDirs, d => Path.GetFileName(d).Contains(cellName, StringComparison.Ordinal));
        return [.. LoadPrimaryLayout(cellDir).Shapes.Where(sh => sh is not ViaShape)];
    }

    /// <summary>Flattened area in mm², at a tolerance far below any pad feature.</summary>
    private static double AreaMm2(LayoutShape shape)
    {
        double total = 0;
        var rings = LayoutFlattener.Flatten(shape, 200);
        for (int r = 0; r < rings.Count; r++)
        {
            double a = 0;
            var ring = rings[r];
            for (int i = 0; i < ring.Length; i += 2)
            {
                int j = (i + 2) % ring.Length;
                a += (double)ring[i] * ring[j + 1] - (double)ring[j] * ring[i + 1];
            }
            total += (r == 0 ? 1 : -1) * Math.Abs(a) / 2.0;
        }
        return total / 1e12;
    }
}
