using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Phase PL1's acceptance gates (docs/sonnet-briefs/brief-PL1-component-library-import.md §14).
///
/// <para>Every fixture under <c>testdata/component-samples/</c> is SYNTHETIC (R-PL1-32): authored from
/// the public format documentation (R-PL1-33), with invented part and terminal names throughout.</para>
///
/// <para>Counters only, never wall clock (gate 19; root <c>CLAUDE.md</c>,
/// <c>feedback-no-new-timing-benchmark-tests</c>).</para>
/// </summary>
public class ComponentImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("component-import-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static string Fixture(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine([dir, "testdata", "component-samples", .. parts]);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture not found: {string.Join('/', parts)}");
    }

    private static ComponentCandidate Candidate(params (string[] Path, ComponentFileKind Kind)[] files)
        => new(ComponentCompleteness.SymbolFootprintAndMap, "test", "test",
               [.. files.Select(f => new ComponentFile(Fixture(f.Path), f.Kind))]);

    private static ComponentPart ReadPart(ComponentCandidate candidate)
    {
        var read = ComponentRead.Read(candidate, Dbu);
        Assert.Null(read.Refusal);
        return read.Part!;
    }

    /// <summary>The nine-terminal fixture, both halves: scrambled symbol pin order, a non-numeric pad
    /// identifier, and art asymmetric on both axes in both views.</summary>
    private static ComponentCandidate Widget9() => Candidate(
        (["widget9", "WIDGET9.kicad_sym"], ComponentFileKind.SymbolSexpr),
        (["widget9", "WIDGET9.kicad_mod"], ComponentFileKind.FootprintSexpr));

    private ComponentImport.ImportResult Import(ComponentPart part, Technology? tech = null)
        => ComponentImport.Import(part, _dir, tech, Dbu);

    private static Symbol LoadSymbol(string cellDir)
        => SymbolPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Symbol),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir)) + ".csym"));

    private static LayoutView LoadLayout(string cellDir, string suffix = "")
        => LayoutPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Layout),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir)) + suffix + ".clay"));

    // ── Gate 2: the folder scan (R-PL1-4, R-PL1-28) ─────────────────────────────────────────────

    /// <summary>
    /// A tree of subfolders, of which two hold readable files and three do not, yields ONE ranked list;
    /// the unreadable ones are counted and named by category.
    ///
    /// <para>Nothing is classified by folder name or by extension (R-PL1-28), and the tree is built to
    /// prove it: the footprint sits in a folder called <c>symbols</c> under the name <c>part.txt</c>,
    /// and the file named <c>part.kicad_sym</c> holds prose. A classifier reading either the folder or
    /// the extension gets both of them backwards.</para>
    /// </summary>
    [Fact]
    public void Gate2_FolderScan_RanksByCompletenessAndNamesWhatItSkipped()
    {
        string root = Path.Combine(_dir, "tree");
        void Put(string relative, string content)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Put("toolA/part.lbr", File.ReadAllText(Fixture("xml", "XLIB4.lbr")));
        Put("toolB/symbols/part.txt", File.ReadAllText(Fixture("widget9", "WIDGET9.kicad_mod")));
        Put("toolB/part.kicad_sym", "This file is a note in plain prose, not a symbol library.\n");
        Put("toolC/model.stp", "ISO-10303-21;\nHEADER;\nENDSEC;\n");
        Put("toolD/drawing.dxf", "  0\nSECTION\n  2\nHEADER\n  0\nENDSEC\n  0\nEOF\n");
        File.WriteAllBytes(Path.Combine(root, "toolE.bin"), [0x00, 0x01, 0x02, 0x00, 0xFF, 0x00]);

        var scan = ComponentFolderScan.Scan(root);

        // The footprint was found by CONTENT, under a .txt name in a folder called "symbols".
        Assert.Contains(scan.Candidates, c => c.FootprintFiles.Any(f => f.Name == "part.txt"));

        // Two readable options: the XML library (complete) and the footprint on its own.
        Assert.Equal(2, scan.Candidates.Count);
        Assert.Equal(ComponentCompleteness.SymbolFootprintAndMap, scan.Candidates[0].Completeness);
        Assert.Equal(ComponentCompleteness.FootprintOnly, scan.Candidates[1].Completeness);

        // Each unreadable category counted and named.
        string skipped = string.Join(" | ", scan.SkippedSummary);
        Assert.Contains("1 binary format", skipped);
        Assert.Contains("1 three-dimensional model", skipped);
        Assert.Contains("1 dimensioned drawing", skipped);
        Assert.Contains("text format", skipped);       // the prose named .kicad_sym
    }

    /// <summary>R-PL1-30: a drawing is classified as a dimensioned DRAWING and never offered as a
    /// component candidate. It carries no pad identifiers and no pin names, so it cannot supply the
    /// pin↔pad map, and it is listed in the skipped summary instead.</summary>
    [Fact]
    public void Gate2b_ADrawingIsNeverOfferedAsAComponent()
    {
        var files = new List<ComponentFile>
        {
            new(Path.Combine(_dir, "part.dxf"), ComponentFileKind.Drawing),
        };
        Assert.Empty(ComponentFolderScan.Rank(files));
        Assert.Contains("dimensioned drawing", string.Join(' ', ComponentFolderScan.Summarize(files)));
    }

    // ── Gate 3: the pin↔pad map (R-PL1-8, R-PL1-10) ─────────────────────────────────────────────

    /// <summary>
    /// The fixture's symbol declares its pins in the order 1, 8, 3, 5, 6, THERMAL, 7, 4, 2 — scrambled
    /// relative to its pad order (R-PL1-10). A reader that walks declaration order fails this test; one
    /// whose symbol order matched its pad order could not catch that defect.
    /// </summary>
    [Fact]
    public void Gate3_ScrambledSymbolOrder_StillNumbersBothViewsTheSame()
    {
        var result = Import(ReadPart(Widget9()));
        var symbol = LoadSymbol(result.CellDir!);
        var layout = LoadLayout(result.CellDir!);

        // The symbol's own declaration order is NOT the port order — proving the fixture bites.
        Assert.Equal([1, 8, 3, 5, 6, 9, 7, 4, 2], symbol.Pins.Select(p => p.PortIndex));

        var padByPin = new Dictionary<string, string>
        {
            ["AIN"] = "1", ["BIN"] = "2", ["VREF"] = "3", ["SEL"] = "4", ["AOUT"] = "5",
            ["BOUT"] = "6", ["VSUP"] = "7", ["CTRL"] = "8", ["PAD"] = "THERMAL",
        };

        foreach (var pin in symbol.Pins)
            Assert.Equal(padByPin[pin.Name!], layout.Pins[pin.PortIndex - 1].Name);
    }

    /// <summary>R-PL1-8's second half: both strings are kept and neither is derived from the other —
    /// the symbol's pin name on the symbol pin, the pad identifier on the layout pin.</summary>
    [Fact]
    public void Gate3b_BothNamesSurvive_NeitherIsDerivedFromTheOther()
    {
        var result = Import(ReadPart(Widget9()));
        var symbol = LoadSymbol(result.CellDir!);
        var layout = LoadLayout(result.CellDir!);

        Assert.Contains(symbol.Pins, p => p.Name == "PAD");            // the symbol's pin name
        Assert.Contains(layout.Pins, p => p.Name == "THERMAL");        // the footprint's pad identifier
        Assert.DoesNotContain(symbol.Pins, p => p.Name == "THERMAL");
        Assert.DoesNotContain(layout.Pins, p => p.Name == "PAD");
    }

    // ── Gate 4: string pad identifiers (R-PL1-9) ────────────────────────────────────────────────

    /// <summary>
    /// Nine terminals, sorted <c>1</c>…<c>8</c> and then the named one.
    ///
    /// <para>A reader that parses pad identifiers as integers drops the non-numeric one silently.</para>
    /// </summary>
    [Fact]
    public void Gate4_NonNumericPadIdentifier_SurvivesAndSortsLast()
    {
        var part = ReadPart(Widget9());
        var terminals = ComponentTerminals.Build(part, part.Footprints[0].PadNames);

        Assert.Equal(9, terminals.Terminals.Count);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "THERMAL"],
                     terminals.Terminals.Select(t => t.PadName));
    }

    /// <summary>The natural sort itself: <c>2</c> precedes <c>10</c>, which a string sort gets
    /// backwards, and the non-numeric tail keeps the order the footprint stated rather than an
    /// alphabetical one it never asked for.</summary>
    [Fact]
    public void Gate4b_NaturalOrder_TwoPrecedesTen_AndTheNamedTailKeepsItsStatedOrder()
    {
        var part = new ComponentPart { Name = "N" };
        var terminals = ComponentTerminals.Build(part, ["10", "2", "SHIELD", "EPAD", "1"]);
        Assert.Equal(["1", "2", "10", "SHIELD", "EPAD"], terminals.Terminals.Select(t => t.PadName));
    }

    // ── Gate 5: unjoined terminals (R-PL1-11) ───────────────────────────────────────────────────

    /// <summary>
    /// A pin bonded to two pads and a pad no pin references both import and are both reported; neither
    /// is dropped and neither is invented.
    ///
    /// <para>The XML fixture states the map as a separate table, so it is the one that can express
    /// this. Its <c>GND@1</c>/<c>GND@2</c> are one logical pin — the <c>@n</c> belongs to the format —
    /// its <c>MH</c> pad is referenced by no symbol pin, and its <c>PD</c> pin joins to no pad, which is
    /// the other direction of the same rule.</para>
    /// </summary>
    [Fact]
    public void Gate5_UnjoinedTerminals_AreImportedAndReported()
    {
        var part = ReadPart(Candidate((["xml", "XLIB4.lbr"], ComponentFileKind.LibraryXml)));
        var terminals = ComponentTerminals.Build(part, part.Footprints[0].PadNames);

        // The bonding suffix is stripped from the name, and both pads survive.
        Assert.Equal("GND", terminals.Terminals.Single(t => t.PadName == "4").PinName);
        Assert.Null(terminals.Terminals.Single(t => t.PadName == "5").PinName);
        Assert.Null(terminals.Terminals.Single(t => t.PadName == "MH").PinName);
        Assert.Equal("PD", terminals.Terminals.Single(t => t.PadName is null).PinName);

        Assert.Equal(1, terminals.PinsWithNoPad);
        Assert.Equal(2, terminals.PadsWithNoPin);

        var result = Import(part);
        Assert.Contains(result.Messages, m =>
            m.Contains("1 symbol pin(s) reference no pad") && m.Contains("2 pad(s) are referenced by no symbol pin"));
    }

    // ── Gate 6: footprint units (R-PL1-14 / R-L4d-2) ────────────────────────────────────────────

    /// <summary>
    /// <c>−1.234567 mm</c> lands on exactly <c>−1234567</c> DBU. <b>The negative case is the test</b>:
    /// <c>(long)(x * 1e6)</c> truncates toward zero, so it is wrong only on the negative side, which is
    /// exactly the bug a fixture drawn in the first quadrant cannot see.
    /// </summary>
    [Fact]
    public void Gate6_NegativeMillimetre_RoundsRatherThanTruncates()
    {
        var read = PcbReader.ReadFootprint(File.ReadAllText(Fixture("widget9", "WIDGET9.kicad_mod")), Dbu);
        Assert.Null(read.Refusal);

        var line = read.Cell!.Shapes.Select(s => s.Shape).OfType<PathShape>()
            .Single(p => p.Xy.Length == 4 && p.Xy[0] == -1234567);
        Assert.Equal([-1234567L, -2500000L, 1234567L, -2500000L], line.Xy);
    }

    // ── Gate 7: two flips, separately (R-PL1-18) ────────────────────────────────────────────────

    /// <summary>
    /// The FOOTPRINT's handedness. The source is +y down and <c>.clay</c> is +y up, so the outline's Y
    /// coordinates are negated. The outline is an L, asymmetric on BOTH axes — geometry symmetric in
    /// either axis imports identically whether the flip happened or not.
    /// </summary>
    [Fact]
    public void Gate7a_FootprintHandedness_IsFlipped()
    {
        var read = PcbReader.ReadFootprint(File.ReadAllText(Fixture("widget9", "WIDGET9.kicad_mod")), Dbu);
        var outline = read.Cell!.Shapes.Select(s => s.Shape).OfType<PathShape>().First(p => p.Xy.Length > 4);

        // Source (-3.5,-2.5) … (-3.5,-0.5), an L opening to the right. Y negated, X untouched.
        Assert.Equal(
            [-3500000L, 2500000L, -1500000L, 2500000L, -1500000L, 1500000L,
             -2500000L, 1500000L, -2500000L,  500000L, -3500000L,  500000L, -3500000L, 2500000L],
            outline.Xy);
    }

    /// <summary>
    /// The SYMBOL's handedness, asserted independently. It flips the other way round: the source symbol
    /// formats are +y UP while <c>.csym</c> is +y DOWN. Flipping either half alone fails one of this
    /// pair.
    /// </summary>
    [Fact]
    public void Gate7b_SymbolHandedness_IsFlippedTheOtherWay()
    {
        var result = Import(ReadPart(Widget9()));
        var symbol = LoadSymbol(result.CellDir!);

        // AIN is stated at +7.62 mm (= +300 mil) in a Y-UP file, so it lands at −300 in a Y-DOWN one.
        var ain = symbol.Pins.Single(p => p.Name == "AIN");
        Assert.Equal(-400, ain.LocalX);
        Assert.Equal(-300, ain.LocalY);

        // And the asymmetric corner mark keeps its handedness: source (-300,400)→(-200,400)→(-200,300).
        var corner = symbol.Primitives.OfType<PolylinePrimitive>().Single();
        Assert.Equal([-300.0, -400.0], corner.Points[0]);
        Assert.Equal([-200.0, -400.0], corner.Points[1]);
        Assert.Equal([-200.0, -300.0], corner.Points[2]);
    }

    // ── Gate 8: exact symbol scale (R-PL1-17) ───────────────────────────────────────────────────

    /// <summary>
    /// A pin at 100 mil in the older epoch and the same pin at 2.54 mm in the newer land on <b>exactly
    /// the same local coordinate</b>, on the connection grid.
    ///
    /// <para>One symbol-editor local unit is one mil: <c>SymbolModel.cs</c> states 100 local units per
    /// connection-grid square and <c>DsnSymbolReader.PinGrid</c> is 100. The newer file converts as
    /// <c>mm / 0.0254</c>, the older 1:1, and the scale is 1 — not chosen, fitted or clamped, so
    /// imported and hand-drawn symbols share one grid.</para>
    /// </summary>
    [Fact]
    public void Gate8_BothSymbolEpochs_LandOnTheSameConnectionGridPoint()
    {
        var newer = Import(ReadPart(Widget9()));
        var older = Import(ReadPart(Candidate(
            (["widget9", "WIDGET9.lib"], ComponentFileKind.SymbolLegacyText),
            (["widget9", "WIDGET9.kicad_mod"], ComponentFileKind.FootprintSexpr))));

        var a = LoadSymbol(newer.CellDir!);
        var b = LoadSymbol(older.CellDir!);

        foreach (var name in new[] { "AIN", "BIN", "VREF", "SEL", "AOUT", "BOUT", "VSUP", "CTRL", "PAD" })
        {
            var pa = a.Pins.Single(p => p.Name == name);
            var pb = b.Pins.Single(p => p.Name == name);
            Assert.Equal(pa.LocalX, pb.LocalX);
            Assert.Equal(pa.LocalY, pb.LocalY);
            Assert.Equal(pa.PortIndex, pb.PortIndex);
        }

        // On the grid EXACTLY — every coordinate a whole number of connection-grid squares.
        Assert.All(a.Pins, p =>
        {
            Assert.Equal(0, p.LocalX % 100);
            Assert.Equal(0, p.LocalY % 100);
        });

        // The specific claim: 2.54 mm and 100 mil are the same 100 local units.
        Assert.Equal(-100, a.Pins.Single(p => p.Name == "VREF").LocalY);
    }

    // ── Gate 9: the pin's free end (R-PL1-19) ───────────────────────────────────────────────────

    /// <summary>
    /// A pin whose stated point is one length outside the body imports with its connection point AT the
    /// stated point, and the body unmoved.
    ///
    /// <para>Getting this wrong puts every pin one pin-length off the body, which presents as a scale
    /// error rather than as the offset it is.</para>
    /// </summary>
    [Fact]
    public void Gate9_StatedPointIsTheFreeEnd_AndTheBodyDoesNotMove()
    {
        var result = Import(ReadPart(Widget9()));
        var symbol = LoadSymbol(result.CellDir!);

        // AIN is stated at x = −10.16 mm = −400 mil, one 2.54 mm length outside a body whose own left
        // edge the file draws at −7.62 mm = −300 mil.
        Assert.Equal(-400, symbol.Pins.Single(p => p.Name == "AIN").LocalX);

        var body = symbol.Primitives.OfType<RectPrimitive>().Single();
        Assert.Equal(600, body.W);                       // −300 … +300, exactly as stated
        Assert.Equal(900, body.H);                       // +400 … −500

        // And the lead the file draws between the two is there, spanning exactly the stated length.
        Assert.Contains(symbol.Primitives.OfType<LinePrimitive>(),
            l => l.X1 == -400 && l.Y1 == -300 && l.X2 == -300 && l.Y2 == -300);
    }

    // ── Gate 10: body graphics (R-PL1-20) ───────────────────────────────────────────────────────

    /// <summary>
    /// A symbol carrying a polygon and an arc imports both, and the arc's sweep DIRECTION is asserted
    /// rather than only its presence.
    ///
    /// <para>The source states its angles counter-clockwise in a +y-up frame; circuitRF's arc primitive
    /// measures them in its own +y-down local frame, so negating Y negates both. The fixture's arc
    /// sweeps −90° in the file and therefore sweeps +90° here.</para>
    /// </summary>
    [Fact]
    public void Gate10_PolygonAndArc_BothImport_AndTheArcKeepsItsDirection()
    {
        var result = Import(ReadPart(Widget9()));
        var symbol = LoadSymbol(result.CellDir!);

        var triangle = symbol.Primitives.OfType<PolygonPrimitive>().Single();
        Assert.True(triangle.Filled);
        Assert.Equal(3, triangle.Points.Count);

        var arc = symbol.Primitives.OfType<ArcPrimitive>().Single();
        Assert.Equal(200, arc.Cy, 3);                    // centre at −5.08 mm, negated
        Assert.Equal(100, arc.R, 3);
        Assert.Equal(90, arc.SweepDeg, 3);               // the file states −90; the sign flip is the point
    }

    /// <summary>The older epoch draws the same arc as a centre, a radius and two angles in TENTHS of a
    /// degree, and lands in the same place. Reading those as whole degrees draws a tenth of the
    /// span.</summary>
    [Fact]
    public void Gate10b_TheOlderEpochsTenthsOfADegree_ProduceTheSameArc()
    {
        var read = ComponentSymbolLegacyReader.Read(File.ReadAllText(Fixture("widget9", "WIDGET9.lib")));
        var arc = read.Part!.Symbol!.Shapes.OfType<KitSymbolArc>().Single();
        Assert.Equal(0, arc.StartDeg, 6);
        Assert.Equal(-90, arc.SweepDeg, 6);
        Assert.Equal(100, arc.Radius, 6);
    }

    // ── Gate 11: both footprint epochs (R-PL1-14) ───────────────────────────────────────────────

    /// <summary>
    /// <c>(module …)</c> and <c>(footprint …)</c> both import; neither is refused for its version.
    /// Dispatch is on the tokens present — a bare <c>(width …)</c> against a <c>(stroke (width …))</c>,
    /// an <c>fp_text reference</c> against a <c>(property "Reference" …)</c> — never on the stamp.
    /// </summary>
    [Fact]
    public void Gate11_BothFootprintEpochs_Import()
    {
        var newer = PcbReader.ReadFootprint(File.ReadAllText(Fixture("widget9", "WIDGET9.kicad_mod")), Dbu);
        var older = PcbReader.ReadFootprint(File.ReadAllText(Fixture("epochs", "OLDPART.kicad_mod")), Dbu);

        Assert.Null(newer.Refusal);
        Assert.Null(older.Refusal);
        Assert.Equal("OLDPART", older.Cell!.LibraryName);
        Assert.Equal(2, older.Cell.Pins.Count);
        Assert.Equal(["1", "2"], older.Cell.Pins.Select(p => p.Pin.Name));
    }

    /// <summary><see cref="PcbReader.Read"/>'s root-tag guard is NOT relaxed (R-PL1-12). A footprint fed
    /// to the board reader is refused by name rather than read as a board with no tracks and no
    /// stackup.</summary>
    [Fact]
    public void Gate11b_TheBoardReaderStillRefusesAFootprint()
    {
        var read = PcbReader.Read(File.ReadAllText(Fixture("widget9", "WIDGET9.kicad_mod")), Dbu);
        Assert.NotNull(read.Refusal);
        Assert.Contains("not a board file", read.Refusal);
    }

    /// <summary>
    /// R-PL1-13, which was the spike's second question: a standalone footprint file has no
    /// <c>(layers …)</c> table, so <c>*.Cu</c> has nothing of its own to expand against.
    ///
    /// <para>The synthesised table declares exactly two copper layers, front and back, so <c>*.Cu</c>
    /// expands to two rather than to however many a board would have. The technical names come from
    /// <c>PcbLayerNaming.TechnicalRows</c>, so <c>*.Mask</c> and <c>*.Paste</c> resolve as they do on a
    /// board.</para>
    /// </summary>
    [Fact]
    public void Gate11c_TheSynthesisedLayerTable_ExpandsTheWildcardsToTwoCopperLayers()
    {
        var table = PcbReader.SynthesiseFootprintLayerTable();
        Assert.Equal(["F.Cu", "B.Cu"], table.Where(e => e.IsCopper).Select(e => e.CanonicalName));
        Assert.Equal(2, PcbReader.ExpandLayerSpec("*.Cu", table).Count);
        Assert.Equal(2, PcbReader.ExpandLayerSpec("*.Mask", table).Count);
        Assert.Equal(2, PcbReader.ExpandLayerSpec("*.Paste", table).Count);

        var read = PcbReader.ReadFootprint(File.ReadAllText(Fixture("thruhole", "THRU.kicad_mod")), Dbu);
        Assert.Null(read.Refusal);
        var layers = read.Cell!.Shapes.Select(s => s.LayerName).Distinct().Order().ToList();
        Assert.Equal(["B.Cu", "Drill", "F.Cu"], layers);
    }

    // ── Gate 12: the named pin length (R-PL1-22) ────────────────────────────────────────────────

    /// <summary>
    /// An XML fixture using each of the four named lengths places all four pins correctly. A numeric
    /// parse yields zero for every name, which collapses all four leads onto the body edge.
    /// </summary>
    [Fact]
    public void Gate12_TheFourNamedPinLengths_AreFourDifferentLengths()
    {
        var read = ComponentLibraryXmlReader.Read(File.ReadAllText(Fixture("xml", "XLIB4.lbr")), Dbu);
        var drawing = read.Part!.Symbol!;

        // point = 0 draws no lead at all; short/middle/long draw 100/200/300 mils.
        var leads = drawing.Shapes.OfType<KitSymbolLine>()
            .Where(l => l.Y1 == l.Y2 && l.X1 == -200)
            .Select(l => l.X2 - l.X1)
            .Order()
            .ToList();
        Assert.Equal([100.0, 200.0, 300.0], leads);

        // And every pin still sits at its own stated terminal, one length outside the body.
        Assert.Equal([200, 100, 0, -100],
            drawing.Pins.Where(p => p.Name.StartsWith('P')).Select(p => p.YMil));
    }

    // ── Gate 13: the layer table is READ, not assumed (R-PL1-21) ────────────────────────────────

    /// <summary>An XML fixture whose layer table is written in a reordered sequence imports identically
    /// to one in conventional order. The numbering is conventional; the table is authoritative, and
    /// everything resolves through it.</summary>
    [Fact]
    public void Gate13_AReorderedLayerTable_ImportsIdentically()
    {
        var a = ReadPart(Candidate((["xml", "XLIB4.lbr"], ComponentFileKind.LibraryXml)));
        var b = ReadPart(Candidate((["xml", "XLIB4-reordered.lbr"], ComponentFileKind.LibraryXml)));

        Assert.Equal(
            a.Footprints[0].Cell.Shapes.Select(s => s.LayerName),
            b.Footprints[0].Cell.Shapes.Select(s => s.LayerName));
        Assert.Equal(
            a.Footprints[0].Cell.Pins.Select(p => $"{p.Pin.Name}@{p.LayerName}"),
            b.Footprints[0].Cell.Pins.Select(p => $"{p.Pin.Name}@{p.LayerName}"));

        Assert.Equal(
            ComponentProvenance.HashOf(a, ComponentTerminals.Build(a, a.Footprints[0].PadNames).Terminals),
            ComponentProvenance.HashOf(b, ComponentTerminals.Build(b, b.Footprints[0].PadNames).Terminals));
    }

    // ── Gate 14: multi-section reported (R-PL1-23) ──────────────────────────────────────────────

    /// <summary>
    /// A two-gate fixture imports gate one and NAMES the other; it is neither merged nor dropped
    /// silently. The same for the second package variant.
    /// </summary>
    [Fact]
    public void Gate14_TheSecondSectionAndTheSecondVariant_AreNamedNotDropped()
    {
        var part = ReadPart(Candidate((["xml", "XLIB4.lbr"], ComponentFileKind.LibraryXml)));

        Assert.Contains("G$2", string.Join(' ', part.UnimportedSections));
        Assert.Contains("-ALT", string.Join(' ', part.UnimportedDeviceVariants));

        // Gate one's pins are what came in, and gate two's are not merged into them.
        Assert.DoesNotContain(part.Symbol!.Pins, p => p.Name == "AUXA");

        var result = Import(part);
        Assert.Contains(result.Messages, m => m.Contains("Not imported: section") && m.Contains("G$2"));
        Assert.Contains(result.Messages, m => m.Contains("Not imported: package variant"));
    }

    // ── Gate 15: density variants (R-PL1-25) ────────────────────────────────────────────────────

    /// <summary>
    /// A part with three land-pattern variants yields <b>one</b> cell with three <c>.clay</c> views and
    /// the nominal one as <c>PrimaryLayout</c>.
    ///
    /// <para>They are density levels of one pattern rather than three parts, so they become sibling
    /// views: separate cells would represent them as separate parts, and importing one would discard the
    /// other two.</para>
    /// </summary>
    [Fact]
    public void Gate15_ThreeDensityVariants_BecomeOneCellWithThreeViews()
    {
        var result = Import(ReadPart(Candidate(
            (["density", "PATTERN.kicad_sym"], ComponentFileKind.SymbolSexpr),
            (["density", "PATTERN-L.kicad_mod"], ComponentFileKind.FootprintSexpr),
            (["density", "PATTERN-M.kicad_mod"], ComponentFileKind.FootprintSexpr),
            (["density", "PATTERN.kicad_mod"], ComponentFileKind.FootprintSexpr))));

        var layoutDir = CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout);
        Assert.Equal(["PATTERN-L.clay", "PATTERN-M.clay", "PATTERN.clay"],
                     Directory.GetFiles(layoutDir, "*.clay").Select(Path.GetFileName).Order());

        var ccell = CellPersistence.LoadFromFile(Path.Combine(result.CellDir!, CellFolder.CcellFileName));
        Assert.Equal("PATTERN.clay", ccell.PrimaryLayout);

        // The three really are different patterns, not three copies of one.
        Assert.NotEqual(LoadLayout(result.CellDir!).Shapes.OfType<RectShape>().First().X1,
                        LoadLayout(result.CellDir!, "-L").Shapes.OfType<RectShape>().First().X1);
    }

    /// <summary>The suffix rule on its own — the nominal pattern is the one that carries none.</summary>
    [Theory]
    [InlineData("PATTERN", "PATTERN", "")]
    [InlineData("PATTERN-M", "PATTERN", "-M")]
    [InlineData("PATTERN-L", "PATTERN", "-L")]
    [InlineData("PATTERN_M", "PATTERN", "_M")]
    public void Gate15b_DensitySuffixes(string fileBaseName, string expectedBase, string expectedVariant)
    {
        var (baseName, variant) = ComponentRead.SplitDensityVariant(fileBaseName);
        Assert.Equal(expectedBase, baseName);
        Assert.Equal(expectedVariant, variant);
    }

    // ── Gate 16: no stackup invented (R-PL1-27) ─────────────────────────────────────────────────

    /// <summary>
    /// The destination technology's stackup is unchanged, and one Messages line names what an EM run
    /// still needs.
    ///
    /// <para>A component file states no permittivity, no thickness and no substrate, so the import
    /// returns layers and nothing else — there is no stackup field on
    /// <see cref="ComponentImport.ImportResult"/> to apply.</para>
    /// </summary>
    [Fact]
    public void Gate16_NoStackupIsInvented_AndTheGapIsNamed()
    {
        var tech = new Technology { Name = "PL1" };
        int before = tech.Stackup.Layers.Count;

        var result = Import(ReadPart(Widget9()), tech);

        Assert.Equal(before, tech.Stackup.Layers.Count);
        Assert.Contains(result.Messages, m =>
            m.Contains("carries no stackup") && m.Contains("still needs a technology"));
    }

    // ── Gate 17: provenance (R-PL1-2) ───────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ImportedFrom</c> round-trips and the source files are present in the cell folder.
    ///
    /// <para>The bytes each view was built from sit beside the cell, and <c>ImportedFrom</c> names the
    /// file and the definition inside it. Nothing resolves through them at runtime.</para>
    /// </summary>
    [Fact]
    public void Gate17_ProvenanceRoundTrips_AndTheSourceFilesAreKept()
    {
        var result = Import(ReadPart(Widget9()));

        Assert.True(File.Exists(Path.Combine(result.CellDir!, "WIDGET9.kicad_sym")));
        Assert.True(File.Exists(Path.Combine(result.CellDir!, "WIDGET9.kicad_mod")));

        var ccell = CellPersistence.LoadFromFile(Path.Combine(result.CellDir!, CellFolder.CcellFileName));
        Assert.NotNull(ccell.ImportedFrom);
        Assert.Equal("WIDGET9.kicad_sym", ccell.ImportedFrom!.Source);
        Assert.Equal("WIDGET9", ccell.ImportedFrom.Definition);
        Assert.Equal(64, ccell.ImportedFrom.ContentHash.Length);

        // The path is never recorded — a .ccell travels, and the sender's absolute path means nothing
        // at any destination it reaches.
        Assert.DoesNotContain(Path.DirectorySeparatorChar, ccell.ImportedFrom.Source);
    }

    /// <summary>R-PL1-7: the free text the file states becomes read-only cell parameters, QUOTED,
    /// because a declared parameter's default is evaluated as an expression and a bare URL is a parse
    /// error. Never parsed, and never used to infer a model.</summary>
    [Fact]
    public void Gate17b_MetadataBecomesReadOnlyQuotedParameters()
    {
        var result = Import(ReadPart(Widget9()));
        var ccell = CellPersistence.LoadFromFile(Path.Combine(result.CellDir!, CellFolder.CcellFileName));

        var datasheet = ccell.Parameters.Single(p => p.Name == "Datasheet");
        Assert.Equal("\"https://example.invalid/widget9.pdf\"", datasheet.DefaultExpression);
        Assert.False(datasheet.ShowOnSchematic);

        Assert.Contains(ccell.Parameters, p => p.Name == "Manufacturer");
        Assert.Contains(ccell.Parameters, p => p.Name == "Description");
        Assert.All(ccell.Parameters, p => Assert.False(p.ShowOnSchematic));
    }

    /// <summary>R-PL1-6: an imported part is a symbol, a footprint and terminals. No schematic view and
    /// no netlist are written.</summary>
    [Fact]
    public void Gate17c_NoSchematicAndNoNetlistAreWritten()
    {
        var result = Import(ReadPart(Widget9()));
        Assert.Empty(Directory.GetFiles(CellFolder.SubFolderPath(result.CellDir!, ViewType.Schematic)));

        var ccell = CellPersistence.LoadFromFile(Path.Combine(result.CellDir!, CellFolder.CcellFileName));
        Assert.Null(ccell.PrimarySchematic);
        Assert.Null(ccell.ExternalNetlistPath);
        Assert.Equal(9, ccell.NumPorts);
    }

    /// <summary>R-PL1-5: importing the same part twice yields <c>PartName_2</c>, the same rule a board
    /// import uses.</summary>
    [Fact]
    public void Gate17d_ASecondImportOfTheSamePart_GetsItsOwnFolder()
    {
        var first = Import(ReadPart(Widget9()));
        var second = Import(ReadPart(Widget9()));

        Assert.Equal("WIDGET9", Path.GetFileName(first.CellDir));
        Assert.Equal("WIDGET9_2", Path.GetFileName(second.CellDir));
    }

    // ── Gate 18: the refusal names the alternatives (R-PL1-29) ──────────────────────────────────

    /// <summary>
    /// A binary input is refused with a sentence naming the four readable extensions, so the message
    /// says what would work rather than only what did not.
    /// </summary>
    [Fact]
    public void Gate18_ABinaryInput_IsRefusedByNamingWhatWeDoRead()
    {
        string path = Path.Combine(_dir, "part.bin");
        File.WriteAllBytes(path, [0x00, 0x11, 0x00, 0x22, 0x00, 0x33]);

        Assert.Equal(ComponentFileKind.Binary, ComponentClassifier.Classify(path));

        var read = ComponentRead.Read(
            new ComponentCandidate(ComponentCompleteness.FootprintOnly, "x", "x",
                                   [new ComponentFile(path, ComponentFileKind.Binary)]),
            Dbu);

        Assert.NotNull(read.Refusal);
        foreach (var extension in ComponentClassifier.ReadableExtensions)
            Assert.Contains(extension, read.Refusal);
    }

    /// <summary>Classification is by CONTENT throughout, and these are the markers it turns on.</summary>
    [Theory]
    [InlineData("(kicad_symbol_lib (version 1))", ComponentFileKind.SymbolSexpr)]
    [InlineData("(footprint \"X\" (layer \"F.Cu\"))", ComponentFileKind.FootprintSexpr)]
    [InlineData("(module X (layer F.Cu))", ComponentFileKind.FootprintSexpr)]
    [InlineData("(kicad_pcb (version 1))", ComponentFileKind.Board)]
    [InlineData("EESchema-LIBRARY Version 2.4\n", ComponentFileKind.SymbolLegacyText)]
    [InlineData("<?xml version=\"1.0\"?><r><drawing><library><packages/></library></drawing></r>", ComponentFileKind.LibraryXml)]
    [InlineData("#VRML V2.0 utf8\n", ComponentFileKind.Model3D)]
    [InlineData("Just some notes about the part.\n", ComponentFileKind.UnreadableText)]
    public void Gate18b_ClassificationIsByContent(string content, ComponentFileKind expected)
        => Assert.Equal(expected, ComponentClassifier.ClassifyContent(
            System.Text.Encoding.UTF8.GetBytes(content), extension: ".unknown"));

    // ── Gate 19: counters only ──────────────────────────────────────────────────────────────────

    /// <summary>Entities read, shapes produced, terminals joined — never wall clock. Nothing in this
    /// phase measures a duration.</summary>
    [Fact]
    public void Gate19_TheSummaryReportsCountersOnly()
    {
        var result = Import(ReadPart(Widget9()));
        Assert.Contains(result.Messages, m => m.Contains("Imported 9 terminal(s) — 9 joined pin to pad"));
        Assert.DoesNotContain(result.Messages, m =>
            m.Contains(" ms") || m.Contains("seconds") || m.Contains("elapsed"));
    }

    // ── R-PL1-15: a pad the EM port picker can select ───────────────────────────────────────────

    /// <summary>
    /// Every pad becomes a <see cref="LayoutPin"/> with a width, a copper layer and an OUTWARD
    /// direction — away from the land pattern's centroid, snapped to the nearest 90° — which is what the
    /// EM port picker selects.
    /// </summary>
    [Fact]
    public void PadsBecomeSelectablePorts_FacingOutOfThePackage()
    {
        var result = Import(ReadPart(Widget9()));
        var layout = LoadLayout(result.CellDir!);

        Assert.Equal(9, layout.Pins.Count);
        Assert.All(layout.Pins, p => Assert.True(p.WidthDbu > 0));

        // Pads 1..4 sit on the left of the package and face left; 5..8 sit on the right and face right.
        Assert.All(layout.Pins.Where(p => p.Name is "1" or "2" or "3" or "4"), p => Assert.Equal(180, p.OutwardDeg));
        Assert.All(layout.Pins.Where(p => p.Name is "5" or "6" or "7" or "8"), p => Assert.Equal(0, p.OutwardDeg));
    }

    // ── R-PL1-24: descriptions carry markup ─────────────────────────────────────────────────────

    /// <summary>R-PL1-24: stripped to plain text for the cell's description parameter — not rendered,
    /// and the markup is not stored.</summary>
    [Fact]
    public void XmlDescriptionMarkup_IsStrippedNotStored()
    {
        var part = ReadPart(Candidate((["xml", "XLIB4.lbr"], ComponentFileKind.LibraryXml)));
        string description = part.Metadata["Description"];

        Assert.Equal("A synthetic two-gate part.", description);
        Assert.DoesNotContain('<', description);
    }

    /// <summary>The bonding suffix belongs to the format. <c>GND@1</c> and <c>GND@2</c> are one logical
    /// pin; a name that merely CONTAINS an <c>@</c> is left alone.</summary>
    [Theory]
    [InlineData("GND@1", "GND")]
    [InlineData("GND@12", "GND")]
    [InlineData("GND", "GND")]
    [InlineData("A@B", "A@B")]
    [InlineData("@1", "@1")]
    public void BondSuffixStripping(string stated, string expected)
        => Assert.Equal(expected, ComponentLibraryXmlReader.StripBondSuffix(stated));
}
