using System.Text;
using Clipper2Lib;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The IPC-2581 reader's artwork (docs/briefs/brief-ipx-2-reader-artwork.md §7). Fixtures in
/// <c>testdata/ipc2581/</c> are written by hand and each states its expected copper, worked by hand, at its head.
/// </summary>
public class Ipc2581ReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ipc2581-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = 1000;
    private const double DbuPerMm = 1e6;

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "ipc2581", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(name);
    }

    private static PcbReader.ReadResult ReadText(string xml, string? step = null, long? ceiling = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return ceiling is { } c
            ? Ipc2581Reader.Read(stream, Dbu, step, c)
            : Ipc2581Reader.Read(stream, Dbu, step);
    }

    private static PcbBoard Board(string xml, string? step = null)
    {
        var read = ReadText(xml, step);
        Assert.Null(read.Refusal);
        return read.Board!;
    }

    /// <summary>A minimal file: one conductor layer and the given content in one set.</summary>
    private static string OneLayer(string setContent, string setAttributes = "", string dictionaries = "") =>
        $"""
        <IPC-2581 revision="C">
          <Content>{dictionaries}</Content>
          <Ecad><CadHeader units="MILLIMETER"/><CadData>
            <Layer name="TOP" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE"/>
            <Step name="board"><LayerFeature layerRef="TOP"><Set {setAttributes}>{setContent}</Set></LayerFeature></Step>
          </CadData></Ecad>
        </IPC-2581>
        """;

    private static IEnumerable<LayoutShape> On(PcbBoard board, string layer) =>
        board.Shapes.Where(s => s.LayerName == layer).Select(s => s.Shape);

    private static Paths64 Union(IEnumerable<LayoutShape> shapes)
    {
        var all = new Paths64();
        foreach (var shape in shapes) all.AddRange(LayoutClipper.ToClipperPaths(shape, 100));
        return Clipper.Union(all, LayoutClipper.Rule);
    }

    private static double AreaMm2(IEnumerable<LayoutShape> shapes) => Clipper.Area(Union(shapes)) / (DbuPerMm * DbuPerMm);

    private static bool IsCopper(IEnumerable<LayoutShape> shapes, double xMm, double yMm)
    {
        var point = new Point64((long)(xMm * DbuPerMm), (long)(yMm * DbuPerMm));
        int inside = 0;
        foreach (var path in Union(shapes))
            if (Clipper.PointInPolygon(point, path) != PointInPolygonResult.IsOutside)
                inside += Clipper.IsPositive(path) ? 1 : -1;
        return inside > 0;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) BoxMm(LayoutShape shape)
    {
        var b = LayoutGeometry.BboxOf(shape);
        return (b.MinX / DbuPerMm, b.MinY / DbuPerMm, b.MaxX / DbuPerMm, b.MaxY / DbuPerMm);
    }

    // ── Gate 1: the same copper from every form ─────────────────────────────────────────────────

    private static void AssertPlainBoardCopper(PcbBoard board)
    {
        Assert.Equal(6.0, AreaMm2(On(board, "TOP")), 6);
        Assert.Equal(140.0, AreaMm2(On(board, "INNER1")), 6);
        Assert.Equal(2.0 + Math.PI / 4, AreaMm2(On(board, "BOTTOM")), 3);

        // The mirrored pad decides where TOP starts: x 0..2 when it is read as mirrored, 4..6 when it is not.
        var boxes = On(board, "TOP").Select(s => BoxMm(s)).ToList();
        Assert.Equal(0.0, boxes.Min(b => b.MinX), 6);
    }

    public static TheoryData<string, string, string> SpellingForms => new()
    {
        { "no revision", " revision=\"A\"", "" },
        { "revision B", "revision=\"A\"", "revision=\"B\"" },
        { "revision C", "revision=\"A\"", "revision=\"C\"" },
        { "another namespace", "http://webstds.ipc.org/2581", "urn:another" },
        { "no namespace", " xmlns=\"http://webstds.ipc.org/2581\"", "" },
        { "copper as SIGNAL", "layerFunction=\"CONDUCTOR\"", "layerFunction=\"SIGNAL\"" },
        { "plane as MIXED", "layerFunction=\"PLANE\"", "layerFunction=\"MIXED\"" },
        { "outline spelling", "BOARD_OUTLINE", "BOARDOUTLINE" },
        { "inner side", "side=\"INTERNAL\"", "side=\"INNER\"" },
        { "capital boolean", "mirror=\"true\"", "mirror=\"TRUE\"" },
        { "boolean as the word", "mirror=\"true\"", "mirror=\"MIRROR\"" },
        { "step-prefixed nets", "net=\"NET1\"", "net=\"board:NET1\"" },
    };

    [Theory]
    [MemberData(nameof(SpellingForms))]
    public void Every_spelling_of_the_plain_board_gives_its_copper(string form, string from, string to)
    {
        Assert.NotNull(form);
        var xml = Fixture("board-plain.xml");
        Assert.Contains(from, xml);
        AssertPlainBoardCopper(Board(xml.Replace(from, to)));
    }

    [Theory]
    [InlineData("board-plain.xml")]
    [InlineData("board-wrapped.xml")]
    [InlineData("board-padstack.xml")]
    public void Each_file_form_gives_the_same_copper(string file) => AssertPlainBoardCopper(Board(Fixture(file)));

    [Fact]
    public void A_step_prefix_comes_off_only_when_it_is_that_step()
    {
        var board = Board(OneLayer("""<Features><RectCorner lowerLeftX="0" lowerLeftY="0" upperRightX="1" upperRightY="1"/></Features>""", "net=\"board:A:B\""));
        Assert.Equal("A:B", board.Shapes[0].Shape.Net);

        board = Board(OneLayer("""<Features><RectCorner lowerLeftX="0" lowerLeftY="0" upperRightX="1" upperRightY="1"/></Features>""", "net=\"other:A\""));
        Assert.Equal("other:A", board.Shapes[0].Shape.Net);
    }

    [Fact]
    public void An_unknown_element_is_counted_and_TextAttribute_draws_nothing()
    {
        var board = Board(OneLayer(
            """
            <Mystery a="1"><Circle diameter="9"/></Mystery>
            <TextAttribute name="k" value="v"/>
            <Features><Circle diameter="1"/></Features>
            """));
        Assert.Equal(1, board.UnknownTokenCounts["Mystery"]);
        Assert.IsType<CircleShape>(Assert.Single(board.Shapes).Shape);
    }

    // ── Gate 2: units ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inch_and_millimetre_copies_agree_exactly_in_DBU()
    {
        static string Describe(PcbImportedShape s) => s.Shape switch
        {
            PathShape p => $"path {string.Join(",", p.Xy)} w{p.Width} {p.End}",
            RectShape r => $"rect {r.X1},{r.Y1},{r.X2},{r.Y2}",
            _ => s.Shape.GetType().Name,
        };

        var mm = Board(Fixture("units-mm.xml")).Shapes.Select(Describe).ToList();
        var inch = Board(Fixture("units-inch.xml")).Shapes.Select(Describe).ToList();
        Assert.Equal(mm, inch);
        Assert.Contains("rect 11430000,24765000,13970000,26035000", mm);
    }

    // ── Gate 3: transform order ────────────────────────────────────────────────────────────────

    [Theory]
    // PAD is x 1..3, y 0..1. A rotation sends (x,y) to (-y,x), a mirror sends (x,y) to (-x,y), and the location is (10,10).
    [InlineData("NONE", 11, 10, 13, 11)]
    [InlineData("ROT", 9, 11, 10, 13)]
    [InlineData("MIR", 7, 10, 9, 11)]
    // Rotated then mirrored: (x,y) -> (-y,x) -> (y,x). The other order would give (-y,-x), which is x 9..10, y 7..9.
    [InlineData("ROTMIR", 10, 11, 11, 13)]
    [InlineData("SCALE", 12, 10, 16, 12)]
    [InlineData("OFFSET", 10, 9.5, 12, 10.5)]
    // Offset 1 gives x 0..2, y 0..1. Rotated: x -1..0, y 0..2. Mirrored: x 0..1, y 0..2. Scaled by 2: x 0..2, y 0..4.
    [InlineData("ALL", 10, 10, 12, 14)]
    public void A_placement_lands_where_the_stated_order_puts_it(string net, double x1, double y1, double x2, double y2)
    {
        var shape = Board(Fixture("xform-order.xml")).Shapes.Single(s => s.Shape.Net == net).Shape;
        Assert.IsType<RectShape>(shape);
        var box = BoxMm(shape);
        Assert.Equal((x1, y1, x2, y2), box);
    }

    [Fact]
    public void A_curve_step_is_an_arc_and_a_mirror_reverses_it()
    {
        const string arc =
            """
            <Polyline><PolyBegin x="1" y="0"/><PolyStepCurve x="0" y="1" centerX="0" centerY="0" clockwise="false"/>
            <LineDesc lineEnd="ROUND" lineWidth="0.1"/></Polyline>
            """;
        var plain = Assert.IsType<PathShape>(Board(OneLayer($"<Features>{arc}</Features>")).Shapes[0].Shape);
        var edge = Assert.Single(plain.Edges!);
        Assert.Equal(EdgeKind.Arc, edge.Kind);
        Assert.Equal(Math.Tan(Math.PI / 8), edge.Bulge, 12);

        var mirrored = Assert.IsType<PathShape>(Board(OneLayer($"""<Features><Xform mirror="true"/>{arc}</Features>""")).Shapes[0].Shape);
        Assert.Equal(-Math.Tan(Math.PI / 8), mirrored.Edges![0].Bulge, 12);
    }

    [Theory]
    [InlineData("ROUND", PathEndStyle.Round)]
    [InlineData("SQUARE", PathEndStyle.Square)]
    [InlineData("NONE", PathEndStyle.Flush)]
    public void Line_ends_map(string word, PathEndStyle end)
    {
        var path = Assert.IsType<PathShape>(Board(OneLayer(
            $"""<Features><Line startX="0" startY="0" endX="1" endY="0"><LineDesc lineEnd="{word}" lineWidth="0.1"/></Line></Features>""")).Shapes[0].Shape);
        Assert.Equal(end, path.End);
        Assert.Equal(100_000, path.Width);
    }

    // ── Primitives ─────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, double> Primitives => new()
    {
        { """<Circle diameter="2"/>""", Math.PI },
        { """<RectCenter width="2" height="3"/>""", 6 },
        { """<RectCorner lowerLeftX="1" lowerLeftY="1" upperRightX="3" upperRightY="2"/>""", 2 },
        { """<RectRound width="4" height="2" radius="1"/>""", 8 - 4 + Math.PI },
        { """<RectRound width="4" height="2" radius="1" upperRight="TRUE" lowerLeft="true" upperLeft="false" lowerRight="false"/>""", 8 - 2 + Math.PI / 2 },
        { """<RectRound width="2" height="1" radius="0" upperRight="true" lowerLeft="true"/>""", 2 },
        { """<RectCham width="4" height="2" chamfer="0.5"/>""", 8 - 4 * 0.125 },
        { """<RectCham width="4" height="2" chamfer="0.5" upperRight="true"/>""", 8 - 0.125 },
        { """<Oval width="4" height="2"/>""", 2 * 2 + Math.PI },
        { """<Oval width="2" height="4"/>""", 2 * 2 + Math.PI },
        { """<Octagon length="2"/>""", 2 * Math.Sqrt(2) },
        { """<Hexagon length="2"/>""", 1.5 * Math.Sqrt(3) },
        { """<Donut shape="ROUND" outerDiameter="4" innerDiameter="2"/>""", Math.PI * 3 },
        { """<Donut shape="SQUARE" outerDiameter="4" innerDiameter="2"/>""", 12 },
        { """<Thermal shape="SQUARE" outerDiameter="4" innerDiameter="2" spokeCount="2" spokeWidth="0.5" spokeStartAngle="0"/>""", 12 - 2 * 0.5 },
        { """<Thermal shape="SQUARE" outerDiameter="4" innerDiameter="2" spokeCount="0"/>""", 12 },
        { """<Butterfly shape="ROUND" diameter="2"/>""", Math.PI / 2 },
        { """<Butterfly shape="SQUARE" side="2"/>""", 2 },
        { """<Moire diameter="4" ringWidth="0.2" ringGap="1" ringNumber="2"/>""", 2 * Math.PI * (2 + 1) * 0.2 },
        { """<Diamond width="2" height="4"/>""", 4 },
        { """<Triangle base="2" height="4"/>""", 4 },
        { """<Ellipse width="2" height="4"/>""", 2 * Math.PI },
    };

    [Theory]
    [MemberData(nameof(Primitives))]
    public void A_primitive_covers_its_stated_area(string primitive, double area)
    {
        // Within 0.02 mm2: a round hole's ring is flat at the import's 1 um tolerance, which leaves each one up to
        // about 0.01 mm2 smaller than the circle it states on these sizes.
        var inline = Board(OneLayer($"<Features>{primitive}</Features>"));
        Assert.Equal(area, AreaMm2(On(inline, "TOP")), 0.02);

        var viaDictionary = Board(OneLayer(
            """<Pad><StandardPrimitiveRef id="P"/></Pad>""",
            dictionaries: $"""<DictionaryStandard units="MILLIMETER"><EntryStandard id="P">{primitive}</EntryStandard></DictionaryStandard>"""));
        Assert.Equal(area, AreaMm2(On(viaDictionary, "TOP")), 0.02);
    }

    [Fact]
    public void An_unresolved_primitive_is_counted_and_draws_nothing()
    {
        var board = Board(OneLayer("""<Pad><StandardPrimitiveRef id="NOPE"/></Pad><Pad padstackDefRef="X"/><Features><Circle/></Features>"""));
        Assert.Empty(board.Shapes);
        Assert.Equal(3, board.SkippedCounts.Values.Sum());
    }

    [Fact]
    public void A_user_primitive_expands_under_its_transform_at_its_own_units()
    {
        const string dictionary =
            """
            <DictionaryUser units="MICRON"><EntryUser id="U"><UserSpecial>
              <RectCorner lowerLeftX="1000" lowerLeftY="0" upperRightX="3000" upperRightY="1000"/>
              <Line startX="0" startY="0" endX="0" endY="5000"><LineDesc lineEnd="NONE" lineWidth="100"/></Line>
            </UserSpecial></EntryUser></DictionaryUser>
            """;
        var board = Board(OneLayer(
            """<Features><Xform rotation="90"/><Location x="10" y="10"/><UserPrimitiveRef id="U"/></Features>""",
            dictionaries: dictionary));
        Assert.Equal((9.0, 11.0, 10.0, 13.0), BoxMm(board.Shapes[0].Shape));
        Assert.Equal(100_000, Assert.IsType<PathShape>(board.Shapes[1].Shape).Width);
    }

    // ── Gate 4: polarity ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_negative_plane_is_its_outline_minus_its_clearances_and_a_thermal_keeps_its_spokes()
    {
        var plane = On(Board(Fixture("negative-plane.xml")), "GND").ToList();
        Assert.Equal(186.0, AreaMm2(plane), 6);

        // One piece, with the clearance and the thermal's four segments as its holes.
        var piece = Assert.IsType<PolygonShape>(Assert.Single(plane));
        Assert.Equal(5, piece.Holes!.Count);

        Assert.True(IsCopper(plane, 1, 1));
        Assert.False(IsCopper(plane, 5, 5));     // the clearance
        Assert.True(IsCopper(plane, 12, 5));     // the thermal's middle
        Assert.True(IsCopper(plane, 13.5, 5));   // a spoke
        Assert.False(IsCopper(plane, 13.5, 6.5));// the ring between spokes
    }

    [Fact]
    public void An_erased_pour_keeps_its_net_on_every_piece_and_shapes_it_misses_are_untouched()
    {
        var top = On(Board(Fixture("positive-erase.xml")), "TOP").ToList();

        var pour = top.Where(s => s.Net == "POUR").ToList();
        Assert.Equal(2, pour.Count);
        Assert.Equal(79.0, AreaMm2(pour), 6);
        Assert.Equal(1.0, AreaMm2(top.Where(s => s.Net == "ISLAND")), 6);
        Assert.IsType<PathShape>(top.Single(s => s.Net == "TRK"));

        Assert.True(IsCopper(top, 5, 5));       // the island, drawn after the erase
        Assert.False(IsCopper(top, 4, 4));      // the erased window
    }

    [Fact]
    public void A_void_fill_clears_only_what_its_own_set_drew()
    {
        var board = Board(
            """
            <IPC-2581><Ecad><CadHeader units="MILLIMETER"/><CadData>
              <Layer name="TOP" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE"/>
              <Step name="b"><LayerFeature layerRef="TOP">
                <Set net="A"><Features><Location x="1" y="1"/><RectCenter width="2" height="2"/></Features></Set>
                <Set net="B">
                  <Features><Location x="5" y="1"/><RectCenter width="2" height="2"/></Features>
                  <Features><Location x="5" y="1"/><RectCenter width="1" height="1"><FillDesc fillProperty="VOID"/></RectCenter></Features>
                </Set>
              </LayerFeature></Step>
            </CadData></Ecad></IPC-2581>
            """);
        Assert.Equal(4.0, AreaMm2(On(board, "TOP").Where(s => s.Net == "A")), 6);
        Assert.Equal(3.0, AreaMm2(On(board, "TOP").Where(s => s.Net == "B")), 6);
    }

    // ── Gate 5: refusals ───────────────────────────────────────────────────────────────────────

    private static string Refusal(string xml, string? step = null, long? ceiling = null)
    {
        var read = ReadText(xml, step, ceiling);
        Assert.Null(read.Board);
        return read.Refusal!;
    }

    private static string TwoSteps(string first, string second) =>
        $"""
        <IPC-2581><Ecad><CadHeader units="MILLIMETER"/><CadData>
          <Layer name="TOP" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE"/>
          {first}
          {second}
        </CadData></Ecad></IPC-2581>
        """;

    private const string BoardStep =
        """<Step name="board"><LayerFeature layerRef="TOP"><Set><Features><Circle diameter="1"/></Features></Set></LayerFeature></Step>""";

    [Fact]
    public void A_file_that_is_not_this_format_is_refused()
    {
        Assert.Contains("<kicad>", Refusal("<kicad/>"));
        Assert.Contains("could not be read as XML", Refusal("(kicad_pcb)"));
        Assert.Contains("could not be read as XML", Refusal("<IPC-2581><Ecad>"));
    }

    [Fact]
    public void A_BOM_only_export_is_refused_quoting_its_function_mode()
    {
        var refusal = Refusal("""<IPC-2581><Content><FunctionMode mode="ASSEMBLY"/></Content><Bom name="x"/></IPC-2581>""");
        Assert.Contains("no Ecad", refusal);
        Assert.Contains("\"ASSEMBLY\"", refusal);
        Assert.Contains("Bom", refusal);
    }

    [Fact]
    public void A_stencil_only_export_is_refused_naming_what_it_holds()
    {
        var refusal = Refusal(
            """
            <IPC-2581><Content><FunctionMode mode="STENCIL"/></Content>
              <Ecad><CadHeader units="MILLIMETER"/><CadData>
                <Layer name="PASTE" layerFunction="SOLDERPASTE" side="TOP" polarity="POSITIVE"/>
                <Step name="board"/>
              </CadData></Ecad></IPC-2581>
            """);
        Assert.Contains("no conductor layer", refusal);
        Assert.Contains("\"STENCIL\"", refusal);
        Assert.Contains("SOLDERPASTE", refusal);
    }

    [Fact]
    public void A_name_does_not_make_a_layer_copper()
    {
        var refusal = Refusal(
            """
            <IPC-2581><Ecad><CadHeader units="MILLIMETER"/><CadData>
              <Layer name="TOP_COPPER" layerFunction="DOCUMENT" side="TOP" polarity="POSITIVE"/>
              <Step name="board"/>
            </CadData></Ecad></IPC-2581>
            """);
        Assert.Contains("no conductor layer", refusal);
    }

    [Fact]
    public void Several_steps_with_none_named_are_refused_listing_them_and_naming_one_reads_it()
    {
        const string coupon = """<Step name="coupon"><LayerFeature layerRef="TOP"><Set><Features><Circle diameter="3"/></Features></Set></LayerFeature></Step>""";
        var xml = TwoSteps(BoardStep, coupon);

        var refusal = Refusal(xml);
        Assert.Contains("'board'", refusal);
        Assert.Contains("'coupon'", refusal);

        Assert.Equal(1_500_000, Assert.IsType<CircleShape>(Board(xml, "coupon").Shapes.Single().Shape).R);
        Assert.Equal(500_000, Assert.IsType<CircleShape>(Board(xml, "board").Shapes.Single().Shape).R);
        Assert.Contains("'other'", Refusal(xml, "other"));
    }

    [Fact]
    public void A_panel_step_is_refused_naming_the_board_it_places()
    {
        const string panel = """<Step name="panel"><StepRepeat stepRef="board" x="0" y="0" nx="2" ny="2" dx="30" dy="30" angle="0"/></Step>""";
        var refusal = Refusal(TwoSteps(panel, BoardStep), "panel");
        Assert.Contains("panel", refusal);
        Assert.Contains("Read 'board' instead", refusal);

        Assert.Contains("a panel placing 'board'", Refusal(TwoSteps(panel, BoardStep)));
    }

    [Theory]
    [InlineData("""<CadHeader/>""", "CadHeader states no units")]
    [InlineData("""<CadHeader units="FURLONG"/>""", "\"FURLONG\"")]
    public void A_section_with_geometry_and_no_usable_unit_is_refused(string header, string expected)
    {
        var refusal = Refusal(OneLayer("""<Features><Circle diameter="1"/></Features>""").Replace("""<CadHeader units="MILLIMETER"/>""", header));
        Assert.Contains(expected, refusal);
    }

    [Theory]
    [InlineData("DictionaryStandard", "EntryStandard", """<Circle diameter="1"/>""")]
    [InlineData("DictionaryUser", "EntryUser", """<UserSpecial/>""")]
    [InlineData("DictionaryLineDesc", "EntryLineDesc", """<LineDesc lineWidth="1"/>""")]
    [InlineData("DictionaryFillDesc", "EntryFillDesc", """<FillDesc fillProperty="FILL"/>""")]
    public void A_dictionary_with_entries_and_no_unit_is_refused(string dictionary, string entry, string body)
    {
        var refusal = Refusal(OneLayer("", dictionaries: $"""<{dictionary}><{entry} id="X">{body}</{entry}></{dictionary}>"""));
        Assert.Contains(dictionary, refusal);
    }

    [Fact]
    public void A_negative_layer_with_no_profile_is_refused_by_name_and_the_rest_imports()
    {
        var board = Board(
            """
            <IPC-2581><Ecad><CadHeader units="MILLIMETER"/><CadData>
              <Layer name="TOP" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE"/>
              <Layer name="PWR" layerFunction="PLANE" side="INTERNAL" polarity="NEGATIVE"/>
              <Step name="b">
                <LayerFeature layerRef="TOP"><Set><Features><Circle diameter="1"/></Features></Set></LayerFeature>
                <LayerFeature layerRef="PWR"><Set><Features><Circle diameter="1"/></Features></Set></LayerFeature>
              </Step>
            </CadData></Ecad></IPC-2581>
            """);
        Assert.Empty(On(board, "PWR"));
        Assert.Single(On(board, "TOP"));
        Assert.Contains(board.Diagnostics, d => d.Contains("'PWR'") && d.Contains("not imported"));
    }

    [Fact]
    public void A_negative_layer_uses_its_own_profile_before_the_steps()
    {
        var board = Board(
            """
            <IPC-2581><Ecad><CadHeader units="MILLIMETER"/><CadData>
              <Layer name="PWR" layerFunction="PLANE" side="INTERNAL" polarity="NEGATIVE">
                <Profile><Polygon><PolyBegin x="0" y="0"/><PolyStepSegment x="2" y="0"/><PolyStepSegment x="2" y="2"/><PolyStepSegment x="0" y="2"/></Polygon></Profile>
              </Layer>
              <Step name="b">
                <Profile><Polygon><PolyBegin x="0" y="0"/><PolyStepSegment x="9" y="0"/><PolyStepSegment x="9" y="9"/><PolyStepSegment x="0" y="9"/></Polygon></Profile>
              </Step>
            </CadData></Ecad></IPC-2581>
            """);
        Assert.Equal(4.0, AreaMm2(On(board, "PWR")), 6);
    }

    [Fact]
    public void A_reference_cycle_is_refused_naming_the_primitive()
    {
        var refusal = Refusal(OneLayer(
            """<Features><UserPrimitiveRef id="LOOP"/></Features>""",
            dictionaries: """<DictionaryUser units="MILLIMETER"><EntryUser id="LOOP"><UserSpecial><UserPrimitiveRef id="LOOP"/></UserSpecial></EntryUser></DictionaryUser>"""));
        Assert.Contains("'LOOP'", refusal);
    }

    [Fact]
    public void Past_the_ceiling_is_a_refusal_with_the_board_readers_advice()
    {
        var refusal = Refusal(OneLayer("""<Features><Circle diameter="1"/></Features><Features><Circle diameter="1"/></Features>"""), ceiling: 1);
        Assert.Contains("Crop the board in the originating tool", refusal);
    }

    // ── Layers ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ordinal_is_stackup_position_and_copper_comes_from_the_function()
    {
        var table = Board(Fixture("layer-order.xml")).LayerTable;
        Assert.Equal(["TOP", "Conductive1", "GND", "BOTTOM", "NOTES", "FILM"],
            table.OrderBy(e => e.Ordinal).Select(e => e.CanonicalName));
        Assert.Equal("signal", table.Single(e => e.CanonicalName == "Conductive1").Type);
        Assert.Equal("power", table.Single(e => e.CanonicalName == "GND").Type);
        Assert.Equal("user", table.Single(e => e.CanonicalName == "NOTES").Type);
    }

    [Fact]
    public void A_stackup_listed_bottom_first_still_orders_top_first()
    {
        var xml = Fixture("layer-order.xml")
            .Replace("\"BOTTOM\" sequence=\"4\"", "\"BOTTOM\" sequence=\"1\"")
            .Replace("\"TOP\" sequence=\"1\"", "\"TOP\" sequence=\"4\"");
        var order = Board(xml).LayerTable.OrderBy(e => e.Ordinal).Select(e => e.CanonicalName).Take(4);
        Assert.Equal(["TOP", "GND", "Conductive1", "BOTTOM"], order);
    }

    [Fact]
    public void A_resistive_layer_is_artwork_and_named_in_the_report()
    {
        var board = Board(Fixture("layer-order.xml"));
        Assert.False(board.LayerTable.Single(e => e.CanonicalName == "FILM").IsCopper);
        Assert.Single(On(board, "FILM"));
        Assert.Contains(board.DegradedCounts.Keys, k => k.Contains("RESISTIVE") && k.Contains("'FILM'"));
    }

    [Fact]
    public void Surfaces_come_from_side_with_no_stackup_to_order_them()
    {
        // BOTTOM comes first in the document and nothing else states an order.
        var xml = Fixture("layer-order.xml");
        int start = xml.IndexOf("<Stackup>", StringComparison.Ordinal);
        int end = xml.IndexOf("</Stackup>", StringComparison.Ordinal) + "</Stackup>".Length;
        var board = Board(xml.Remove(start, end - start));

        var (layers, _) = PcbLayerReconciliation.BuildSourceLayers(["TOP", "BOTTOM", "GND", "Conductive1"], board.LayerTable, null);
        string Alias(string name) => layers.Single(l => l.Name == name).Interchange!.PcbLayerName!;
        Assert.Equal("F.Cu", Alias("TOP"));
        Assert.Equal("B.Cu", Alias("BOTTOM"));
        Assert.StartsWith("In", Alias("GND"));
        Assert.StartsWith("In", Alias("Conductive1"));
    }

    [Fact]
    public void A_layer_reconciles_onto_a_technology_by_what_it_is_not_what_it_is_called()
    {
        var board = Board(Fixture("layer-order.xml"));
        var tech = new Technology();
        var key = new LayerKey(7, 0);
        tech.Layers.Add(new LayerDef { Key = key, Name = "Front metal", Interchange = new InterchangeMapping(null, null, null, null, null, "F.Cu") });

        var (_, keys) = PcbLayerReconciliation.BuildSourceLayers(["TOP", "BOTTOM"], board.LayerTable, tech);
        Assert.Equal(key, keys["TOP"]);
        Assert.NotEqual(key, keys["BOTTOM"]);
    }

    [Fact]
    public void The_profile_lands_on_the_outline_layer_whichever_way_it_is_spelled()
    {
        var plain = Board(Fixture("board-plain.xml"));
        Assert.Equal("Edge.Cuts", plain.LayerTable.Single(e => e.CanonicalName == "OUTLINE").InterchangeName);
        Assert.IsType<PathShape>(Assert.Single(On(plain, "OUTLINE")));

        var other = Board(Fixture("board-plain.xml").Replace("BOARD_OUTLINE", "BOARDOUTLINE"));
        Assert.Single(On(other, "OUTLINE"));

        Assert.Equal("F.SilkS", plain.LayerTable.Single(e => e.CanonicalName == "SILK").InterchangeName);

        // No outline layer in the file: one is added, recognised as the outline.
        var without = Board(Fixture("negative-plane.xml"));
        Assert.Equal("Edge.Cuts", without.LayerTable.Single(e => e.CanonicalName == Ipc2581Reader.SyntheticOutlineLayer).InterchangeName);
    }

    [Fact]
    public void A_board_imports_through_the_seam_with_its_layers_named_by_their_roles()
    {
        var board = Board(Fixture("layer-order.xml"));
        var result = PcbImport.ImportBoard(board, _dir, "b", null, Dbu);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.BoardCellDir);
        Assert.Single(result.CreatedCellDirs);

        string Alias(string name) => result.LayersToAdd.Single(l => l.Name == name).Interchange!.PcbLayerName!;
        Assert.Equal("F.Cu", Alias("TOP"));
        Assert.Equal("In1.Cu", Alias("Conductive1"));
        Assert.Equal("In2.Cu", Alias("GND"));
        Assert.Equal("B.Cu", Alias("BOTTOM"));
    }
}
