using System.Text;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The IPC-2581 reader's nets, parts, holes and stackup (docs/briefs/brief-ipx-3-nets-vias-stackup.md §6). Fixtures in
/// <c>testdata/ipc2581/</c> are written by hand, and each states at its head what it holds.
/// </summary>
public sealed class Ipc2581NetsViasStackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ipc2581-nets-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = 1000;
    private const long Mm = 1_000_000;

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

    private static PcbBoard Board(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var read = Ipc2581Reader.Read(stream, Dbu);
        Assert.Null(read.Refusal);
        return read.Board!;
    }

    private static IEnumerable<LayoutShape> On(PcbBoard board, string layer) =>
        board.Shapes.Where(s => s.LayerName == layer).Select(s => s.Shape);

    /// <summary>The one shape whose box holds this point, on a layer.</summary>
    private static LayoutShape At(PcbBoard board, string layer, double xMm, double yMm) =>
        Assert.Single(On(board, layer), s =>
        {
            var b = LayoutGeometry.BboxOf(s);
            long x = (long)(xMm * Mm), y = (long)(yMm * Mm);
            return b.MinX <= x && x <= b.MaxX && b.MinY <= y && y <= b.MaxY;
        });

    // ── Gate 1: nets and parts ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_net_lands_on_tracks_pads_and_vias_with_the_step_prefix_off_and_a_no_net_name_stays_null()
    {
        var board = Board(Fixture("nets-parts.xml"));
        Assert.Equal("TRACK", At(board, "TOP", 2, 1).Net);
        Assert.Equal("SIG6", At(board, "TOP", 20, 1).Net);
        Assert.Null(At(board, "TOP", 22, 1).Net);
        Assert.Null(At(board, "TOP", 24, 1).Net);
        Assert.Equal("STACK_NET", At(board, "TOP", 26, 1).Net);
        Assert.Equal("VIA_NET", Assert.IsType<ViaShape>(Assert.Single(board.Shapes, s => s.Shape is ViaShape).Shape).Net);
    }

    [Theory]
    [InlineData(10, "U1", "1")]   // a PinRef, with the step prefix on its reference
    [InlineData(12, "U1", "2")]   // attributes on the pad
    [InlineData(14, "R1", "5")]   // a stated part, whose pin the logical net settles
    [InlineData(16, "C1", "7")]   // a stated pin, whose part the logical net settles
    [InlineData(26, "U1", "4")]   // a pad stack's PinRef
    [InlineData(28, "U1", "9")]   // both halves in the pin, which counts because the file names U1
    public void A_pad_knows_its_part_from_each_spelling(double x, string component, string pin)
    {
        var pad = At(Board(Fixture("nets-parts.xml")), "TOP", x, 1);
        Assert.Equal(component, pad.Component);
        Assert.Equal(pin, pad.Pin);
    }

    [Fact]
    public void A_pad_nothing_settles_keeps_only_what_it_states_and_no_location_is_matched()
    {
        var board = Board(Fixture("nets-parts.xml"));

        // L1 has two pins on this net, so the net does not say which.
        var two = At(board, "TOP", 18, 1);
        Assert.Equal("L1", two.Component);
        Assert.Null(two.Pin);
        Assert.Contains(board.DegradedCounts.Keys, k => k.Contains("no logical net settled"));

        // The file names no part XX, so this pin is left as written and not split.
        var unsplit = At(board, "TOP", 29.5, 1);
        Assert.Null(unsplit.Component);
        Assert.Equal("XX.9", unsplit.Pin);

        // U1 is placed at (2, 3), and a pad states nothing about it.
        var silent = At(board, "TOP", 20, 1);
        Assert.Null(silent.Component);
        Assert.Null(silent.Pin);
    }

    [Fact]
    public void A_pour_and_a_pad_keep_their_net_and_part_on_every_piece_an_erase_leaves()
    {
        var board = Board(Fixture("nets-parts.xml"));
        Assert.Equal("POUR", At(board, "BOTTOM", 2, 2).Net);
        Assert.Equal("POUR", At(board, "BOTTOM", 8, 2).Net);
        Assert.DoesNotContain(On(board, "BOTTOM"), s => s.Net == "ISLAND");

        foreach (double x in new[] { 13.5, 16.5 })
        {
            var piece = At(board, "BOTTOM", x, 2);
            Assert.Equal(("PIN_PAD", "U1", "3"), (piece.Net, piece.Component, piece.Pin));
        }
    }

    [Fact]
    public void Nets_no_copper_carries_are_counted_and_are_not_nets_on_the_board()
    {
        var board = Board(Fixture("nets-parts.xml"));
        Assert.Contains(board.Diagnostics, d => d.StartsWith("2 net(s) this file states are carried by no copper", StringComparison.Ordinal));
        Assert.DoesNotContain(board.Shapes, s => s.Shape.Net is "NO_COPPER_1" or "NO_COPPER_2");
    }

    [Fact]
    public void A_component_is_read_with_its_reference_side_and_position_and_reported_as_not_placed()
    {
        var board = Board(Fixture("nets-parts.xml"));
        Assert.Equal(new PcbStatedComponent("U1", "TOP", 2 * Mm, 3 * Mm, 90), board.Components[0]);
        Assert.Equal(new PcbStatedComponent("R1", "BOTTOM", 4 * Mm, 3 * Mm, 0), board.Components[1]);
        Assert.Empty(board.Placements);
        Assert.Contains(board.Diagnostics, d => d.StartsWith("2 component(s) were read and not placed as instances", StringComparison.Ordinal));
    }

    // ── Gate 2: a negative plane's net ──────────────────────────────────────────────────────────

    [Fact]
    public void A_split_plane_names_each_piece_from_the_points_on_it_and_a_clearance_names_nothing()
    {
        var board = Board(Fixture("plane-nets.xml"));
        Assert.Equal("LEFT", At(board, "SPLIT", 5, 5).Net);
        Assert.Equal("RIGHT", At(board, "SPLIT", 15, 5).Net);
    }

    [Fact]
    public void A_plane_with_no_evidence_has_no_net_whatever_the_layer_is_called()
    {
        var board = Board(Fixture("plane-nets.xml"));
        Assert.Null(Assert.Single(On(board, "GND")).Net);
        Assert.Contains(board.DegradedCounts.Keys, k => k.Contains("negative layer 'GND'"));
    }

    [Fact]
    public void A_plane_with_conflicting_evidence_has_no_net_and_reports_the_layer_and_the_nets()
    {
        var board = Board(Fixture("plane-nets.xml"));
        Assert.Null(Assert.Single(On(board, "CONFLICT")).Net);
        Assert.Contains(board.Diagnostics, d => d.Contains("'CONFLICT'") && d.Contains("ONE, TWO"));
    }

    [Fact]
    public void A_thermal_names_the_plane_it_joins_its_pin_to()
    {
        Assert.Equal("PWR", Assert.Single(On(Board(Fixture("plane-nets.xml")), "THERMAL")).Net);
    }

    // ── Gate 3: holes and vias ──────────────────────────────────────────────────────────────────

    private static PcbImportedShape ViaAt(PcbBoard board, double x) =>
        Assert.Single(board.Shapes, s => s.Shape is ViaShape v && v.X == (long)(x * Mm));

    [Theory]
    [InlineData(1, "L1", "L4", "L1")]   // a drill layer with no span runs through the board
    [InlineData(2, "L1", "L2", "L1")]   // the hole's own span
    [InlineData(3, "L2", "L3", "L2")]   // the layer's span, top first, and the layer's name is not read
    [InlineData(8, "L1", "L4", "L1")]   // a pad stack
    [InlineData(9, "L3", "L4", "L3")]   // PLATED, but a span short of the board is no component hole
    public void A_via_takes_its_span_from_the_stated_source_and_its_pad_from_the_top_of_it(double x, string from, string to, string landing)
    {
        var via = ViaAt(Board(Fixture("holes-vias.xml")), x);
        Assert.Equal((from, to, landing), (via.SpanFromName, via.SpanToName, via.LandingLayerName));
    }

    [Fact]
    public void A_via_consumes_one_pad_so_no_copper_is_drawn_twice_and_its_other_pads_stay()
    {
        var board = Board(Fixture("holes-vias.xml"));
        var via = Assert.IsType<ViaShape>(ViaAt(board, 1).Shape);
        Assert.Equal((600_000L, 300_000L, "N1"), (via.PadSize, via.DrillSize, via.Net));

        Assert.DoesNotContain(On(board, "L1"), s => s is CircleShape c && c.Cx == Mm);
        Assert.Single(On(board, "L4"), s => s is CircleShape c && c.Cx == Mm);
    }

    [Fact]
    public void A_plated_hole_stays_a_hole_with_its_pads_and_a_non_plated_hole_stays_a_hole()
    {
        var board = Board(Fixture("holes-vias.xml"));
        var plated = Assert.IsType<CircleShape>(Assert.Single(On(board, "DRILL_ALL"), s => s is CircleShape c && c.Cx == 6 * Mm));
        Assert.Equal((400_000L, "N6"), (plated.R, plated.Net));
        Assert.Single(On(board, "L1"), s => s is CircleShape c && c.Cx == 6 * Mm);
        Assert.Single(On(board, "L4"), s => s is CircleShape c && c.Cx == 6 * Mm);

        Assert.Single(On(board, "DRILL_ALL"), s => s is CircleShape c && c.Cx == 7 * Mm);
    }

    [Fact]
    public void A_via_with_no_round_pad_stays_a_hole_beside_its_copper_with_a_count()
    {
        var board = Board(Fixture("holes-vias.xml"));
        foreach (double x in new[] { 4.0, 5.0 })
            Assert.Single(On(board, "DRILL_ALL"), s => s is CircleShape c && c.Cx == (long)(x * Mm));
        Assert.IsType<RectShape>(At(board, "L1", 5, 1));
        Assert.Equal(2, board.DegradedCounts.Single(k => k.Key.StartsWith("via with no round copper pad", StringComparison.Ordinal)).Value);
    }

    [Fact]
    public void A_slot_is_one_path_a_shape_that_cannot_be_one_is_counted_and_a_backdrill_is_reported()
    {
        var board = Board(Fixture("holes-vias.xml"));
        var slot = Assert.IsType<PathShape>(Assert.Single(On(board, "ROUTE")));
        Assert.Equal([19 * Mm, 5 * Mm, 21 * Mm, 5 * Mm], slot.Xy);
        Assert.Equal(1 * Mm, slot.Width);
        Assert.Equal(1, board.SkippedCounts["slot or cavity whose shape is not a path"]);
        Assert.Contains(board.Diagnostics, d => d.StartsWith("1 backdrill specification(s)", StringComparison.Ordinal));
    }

    /// <summary>R-ipx3-4e, through the saved document and the resolver every consumer asks.</summary>
    [Fact]
    public void A_saved_via_reaches_every_layer_of_its_span_and_a_drill_layer_is_a_drill_layer()
    {
        var r = PcbImport.ImportBoard(Board(Fixture("holes-vias.xml")), _dir, "holes", null, Dbu);

        var tech = new Technology { Name = "t", Stackup = r.Stackup!, Layers = [.. r.LayersToAdd] };
        tech.Stackup.Layers.AddRange(r.ViaEntries);

        string cell = r.BoardCellDir!;
        var view = LayoutPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(cell, ViewType.Layout), Path.GetFileName(cell) + ".clay"));

        var buried = Assert.Single(view.Shapes.OfType<ViaShape>(), v => v.X == 3 * Mm);
        var span = ViaSpanResolver.Resolve(buried.Layer, tech);
        Assert.Equal(("L2", "L3"), (span!.Top.Name, span.Bottom.Name));
        Assert.False(ViaSpanResolver.IsThrough(span, tech));

        var through = ViaSpanResolver.Resolve(Assert.Single(view.Shapes.OfType<ViaShape>(), v => v.X == 1 * Mm).Layer, tech);
        Assert.True(ViaSpanResolver.IsThrough(through!, tech));

        Assert.Equal("drill", r.LayersToAdd.Single(l => l.Name == "DRILL_ALL").Purpose);
    }

    // ── Gate 4: the stackup ─────────────────────────────────────────────────────────────────────

    private static string BoardWithStackup(string stackup, string layers = "", string header = "") => $"""
        <IPC-2581 revision="C">
          <Content/>
          <Ecad>
            <CadHeader units="MILLIMETER">{header}</CadHeader>
            <CadData>
              <Layer name="TOP" layerFunction="CONDUCTOR" side="TOP" polarity="POSITIVE"/>
              <Layer name="BOT" layerFunction="CONDUCTOR" side="BOTTOM" polarity="POSITIVE"/>
              {layers}
              {stackup}
              <Step name="board"><LayerFeature layerRef="TOP"><Set net="A"><Features><RectCorner lowerLeftX="0" lowerLeftY="0" upperRightX="1" upperRightY="1"/></Features></Set></LayerFeature></Step>
            </CadData>
          </Ecad>
        </IPC-2581>
        """;

    private static Stackup Mapped(PcbBoard board)
    {
        var built = PcbStackupMapping.Build(
            board.Stackup, board.OverallThicknessMm, Dbu, _ => null, board.CompleteStackupFromDefaults, board.StackupRefusal);
        return built.Stackup!;
    }

    [Fact]
    public void Nested_groups_walk_top_first_and_a_mask_is_skipped_and_counted()
    {
        var board = Board(Fixture("stackup-values.xml"));
        Assert.Equal(
            ["TOP", "CORE", "MID", "PREG", "BOT"],
            board.Stackup!.Where(e => e.Type != "non-electrical").Select(e => e.Name));
        Assert.Contains(PcbStackupMapping.Build(board.Stackup, null, Dbu, _ => null).Messages, m => m.Contains("1 non-electrical entry"));
    }

    [Fact]
    public void Thickness_comes_in_the_headers_units_and_overall_thickness_is_the_boards()
    {
        var board = Board(Fixture("stackup-values.xml"));
        Assert.Equal(1.016, board.OverallThicknessMm!.Value, 9);
        var core = Mapped(board).Layers.Single(l => l.Name == "CORE");
        Assert.Equal(508_000, core.ThicknessDbu);
        Assert.Equal(1_016_000, Mapped(board).BoardThicknessDbu);
    }

    [Fact]
    public void A_sum_more_than_five_percent_from_the_overall_thickness_is_reported()
    {
        var board = Board(Fixture("stackup-values.xml").Replace("overallThickness=\"0.04\"", "overallThickness=\"0.05\""));
        Assert.Contains(board.Diagnostics, d => d.Contains("more than 5%"));
        Assert.DoesNotContain(Board(Fixture("stackup-values.xml")).Diagnostics, d => d.Contains("more than 5%"));

        // Conductors with no dielectric listed between them: their sum is short by construction, not a mismatch.
        var unlisted = Board(BoardWithStackup(
            """
            <Stackup overallThickness="1.6">
              <StackupGroup stackupGroupName="TOP" thickness="0.03"/>
              <StackupGroup stackupGroupName="BOT" thickness="0.03"/>
            </Stackup>
            """));
        Assert.DoesNotContain(unlisted.Diagnostics, d => d.Contains("more than 5%"));
    }

    [Fact]
    public void Spec_values_are_used_and_a_table_takes_its_lowest_frequency_and_lists_every_pair()
    {
        var board = Board(Fixture("stackup-values.xml"));
        var preg = board.Stackup!.Single(e => e.Name == "PREG");
        Assert.Equal((3.6, 0.004), (preg.EpsilonR, preg.LossTangent));

        var core = board.Stackup!.Single(e => e.Name == "CORE");
        Assert.Equal((4.1, 0.009), (core.EpsilonR, core.LossTangent));
        Assert.Contains(board.Diagnostics, d => d.Contains("'CORE'") && d.Contains("3.9 at 10 GHz") && d.Contains("4.1 at 500 MHz"));
        Assert.Contains(board.Diagnostics, d => d.Contains("'CORE'") && d.Contains("85 CELCIUS"));
    }

    [Fact]
    public void A_conductivity_converts_by_its_stated_unit_and_an_unknown_unit_is_missing_and_reported()
    {
        var board = Board(Fixture("stackup-values.xml"));
        Assert.Equal(5e7, board.Stackup!.Single(e => e.Name == "TOP").ConductivitySm);
        Assert.Equal(4e7, board.Stackup!.Single(e => e.Name == "MID").ConductivitySm);
        Assert.Null(board.Stackup!.Single(e => e.Name == "BOT").ConductivitySm);
        Assert.Contains(board.Diagnostics, d => d.Contains("'BOT'") && d.Contains("OTHER"));
    }

    [Fact]
    public void Roughness_an_impedance_target_and_a_port_are_reported_and_not_used()
    {
        var board = Board(Fixture("stackup-values.xml"));
        Assert.Contains(board.Diagnostics, d => d.StartsWith("1 conductor(s) state a surface roughness", StringComparison.Ordinal));
        Assert.Contains(board.Diagnostics, d => d.StartsWith("1 impedance specification(s)", StringComparison.Ordinal));
        Assert.Equal(1, board.SkippedCounts["Port (not modelled)"]);
    }

    [Fact]
    public void An_exporters_attribute_gives_values_with_a_mho_per_cm_conversion_and_says_so()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"><NonstandardAttribute name="LAYER_ELECTRICAL_CONDUCTIVITY" type="STRING" value="5.8e5 mho/cm"/></StackupLayer>
              <StackupLayer layerOrGroupRef="D" thickness="1">
                <NonstandardAttribute name="LAYER_DIELECTRIC_CONSTANT" type="DOUBLE" value="4.2"/>
                <NonstandardAttribute name="LAYER_LOSS_TANGENT" type="DOUBLE" value="0.015"/>
              </StackupLayer>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"><NonstandardAttribute name="LAYER_ELECTRICAL_CONDUCTIVITY" type="STRING" value="5.8e7"/></StackupLayer>
            </Stackup>
            """));

        Assert.Equal(5.8e7, board.Stackup!.Single(e => e.Name == "TOP").ConductivitySm!.Value, 3);
        var d = board.Stackup!.Single(e => e.Name == "D");
        Assert.Equal((4.2, 0.015), (d.EpsilonR, d.LossTangent));

        Assert.Null(board.Stackup!.Single(e => e.Name == "BOT").ConductivitySm);
        Assert.Contains(board.Diagnostics, m => m.Contains("exporter's attribute") && m.Contains("'TOP'") && m.Contains("'D'"));
        Assert.Contains(board.Diagnostics, m => m.Contains("'BOT'") && m.Contains("treated as missing"));
    }

    [Fact]
    public void A_placeholder_stackup_is_structure_plus_named_defaults_and_nothing_stays_at_air()
    {
        // A conductor as a group named after its layer, with nothing electrical inside and no dielectric listed.
        var board = Board(BoardWithStackup(
            """
            <Stackup overallThickness="1">
              <StackupGroup stackupGroupName="TOP" thickness="0"><StackupLayer layerOrGroupRef="NOTES" thickness="0"/></StackupGroup>
              <StackupGroup stackupGroupName="BOT" thickness="0"/>
            </Stackup>
            """,
            """<Layer name="NOTES" layerFunction="GRAPHIC" side="NONE" polarity="POSITIVE"/>"""));

        var built = PcbStackupMapping.Build(
            board.Stackup, board.OverallThicknessMm, Dbu, _ => null, board.CompleteStackupFromDefaults);
        var layers = built.Stackup!.Layers;

        Assert.Equal([StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor], layers.Select(l => l.Kind));
        Assert.Equal(SubstrateDefaults.Epsr, layers[1].Epsr);
        Assert.Equal(SubstrateDefaults.TanD, layers[1].TanD);
        Assert.Equal(930_000, layers[1].ThicknessDbu);    // 1 mm less two 35 um conductors
        Assert.All(layers, l => Assert.True(l.ThicknessDbu > 0));

        Assert.Contains(built.Messages, m => m.Contains("states its structure and no values"));
        Assert.Contains(built.Messages, m => m.Contains("relative permittivity 4.4") && m.Contains("loss tangent 0.02") && m.Contains("copper thickness"));
        Assert.Contains(board.Diagnostics, m => m.Contains("a dielectric with no stated values was added"));
    }

    [Fact]
    public void An_undeclared_name_between_copper_with_a_thickness_is_a_dielectric_and_a_coating_outside_is_not()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="COATING" thickness="0.02" materialType="FR-4"/>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="MIDDLE" thickness="1" materialType="COPPER"/>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"/>
            </Stackup>
            """));

        Assert.Equal(
            [("COATING", "non-electrical"), ("TOP", "copper"), ("MIDDLE", "core"), ("BOT", "copper")],
            board.Stackup!.Select(e => (e.Name, e.Type)));
    }

    [Fact]
    public void A_layer_type_attribute_decides_a_name_with_no_layer()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="BOARD_CORE" thickness="0"><NonstandardAttribute name="LAYER_TYPE" type="STRING" value="DIELCORE"/></StackupLayer>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"/>
            </Stackup>
            """));
        Assert.Equal("core", board.Stackup!.Single(e => e.Name == "BOARD_CORE").Type);
    }

    [Fact]
    public void A_stackup_listed_bottom_first_is_reversed_and_reported()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="D" thickness="1"/>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"/>
            </Stackup>
            """));
        Assert.Equal(["TOP", "D", "BOT"], board.Stackup!.Select(e => e.Name));
        Assert.Contains(board.Diagnostics, m => m.Contains("was reversed"));
    }

    [Fact]
    public void A_stackup_whose_ends_disagree_with_their_sides_is_refused_and_the_artwork_is_kept()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="INNER1" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="INNER2" thickness="0.03"/>
            </Stackup>
            """,
            """
            <Layer name="INNER1" layerFunction="SIGNAL" side="INTERNAL" polarity="POSITIVE"/>
            <Layer name="INNER2" layerFunction="SIGNAL" side="INTERNAL" polarity="POSITIVE"/>
            """));

        Assert.Null(board.Stackup);
        Assert.Contains("'INNER1' first", board.StackupRefusal);
        Assert.Single(On(board, "TOP"));

        var built = PcbStackupMapping.Build(board.Stackup, null, Dbu, _ => null, refusal: board.StackupRefusal);
        Assert.Null(built.Stackup);
        Assert.Contains(built.Messages, m => m.Contains("was not used") && m.Contains("'INNER1' first"));
    }

    [Fact]
    public void Copper_the_stackup_does_not_place_is_a_refusal_naming_it()
    {
        var board = Board(BoardWithStackup(
            """
            <Stackup>
              <StackupLayer layerOrGroupRef="TOP" thickness="0.03"/>
              <StackupLayer layerOrGroupRef="BOT" thickness="0.03"/>
            </Stackup>
            """,
            """<Layer name="LOST" layerFunction="PLANE" side="INTERNAL" polarity="POSITIVE"/>"""));
        Assert.Null(board.Stackup);
        Assert.Contains("'LOST'", board.StackupRefusal);
    }

    [Fact]
    public void With_zones_the_largest_zones_stackup_is_used_and_every_zone_is_named()
    {
        static string Zone(string name, string stackup, int width) => $"""
            <StackupZone name="{name}" stackupRef="{stackup}"><Profile><Polygon>
              <PolyBegin x="0" y="0"/><PolyStepSegment x="{width}" y="0"/><PolyStepSegment x="{width}" y="1"/><PolyStepSegment x="0" y="1"/><PolyStepSegment x="0" y="0"/>
            </Polygon></Profile></StackupZone>
            """;

        var xml = BoardWithStackup(
            """
            <Stackup name="FLEX" overallThickness="0.2"><StackupLayer layerOrGroupRef="TOP" thickness="0.01"/><StackupLayer layerOrGroupRef="BOT" thickness="0.01"/></Stackup>
            <Stackup name="RIGID" overallThickness="1.6"><StackupLayer layerOrGroupRef="TOP" thickness="0.03"/><StackupLayer layerOrGroupRef="BOT" thickness="0.03"/></Stackup>
            """)
            .Replace("<Step name=\"board\">", "<Step name=\"board\">" + Zone("Tail", "FLEX", 2) + Zone("Main", "RIGID", 9));

        var board = Board(xml);
        Assert.Equal(1.6, board.OverallThicknessMm);
        Assert.Contains(board.Diagnostics, m => m.Contains("Tail, Main") && m.Contains("largest zone, 'Main'"));
    }

    /// <summary>Stated values reach a technology unchanged, and no default is applied over one.</summary>
    [Fact]
    public void A_stated_stackup_maps_with_nothing_filled()
    {
        var built = PcbStackupMapping.Build(
            [
                new PcbStackupEntry("TOP", "copper", 0.03, null, null, 5e7),
                new PcbStackupEntry("D", "core", 1.0, 3.6, 0.004),
                new PcbStackupEntry("BOT", "copper", 0.03, null, null, 5e7),
            ],
            null, Dbu, _ => null, completeMissing: true);

        var d = built.Stackup!.Layers[1];
        Assert.Equal((3.6, 0.004, 1_000_000L), (d.Epsr, d.TanD, d.ThicknessDbu));
        Assert.DoesNotContain(built.Messages, m => m.Contains("NOT STATED BY ANY FILE"));
    }
}
