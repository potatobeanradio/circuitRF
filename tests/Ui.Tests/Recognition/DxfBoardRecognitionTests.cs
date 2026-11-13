// ================================================================
//  DxfBoardRecognitionTests.cs — a two-layer RF board drawn as DXF outlines, imported with a layer map and recognised
//  into the circuit it is, with no hand correction, from every spelling other CAD programs write a DXF in.
//
//  testdata/interchange/dxf-board/make_boards.py draws the board and says how each file spells it (R12 polylines and
//  TEXT; R2000 bulged polylines, hatches, MTEXT and nested blocks; inches; no units; LINE and ARC strokes; the mirror
//  image). No part has a land: each is a silkscreen body over a gap in the copper, named by a typed designator.
//  The expected circuit below is written by hand from that design, as topology — which designators and ports share
//  each node — and each line's width and length, never node names.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DxfBoardRecognitionTests : IDisposable
{
    private const string Map = "COPPER_TOP=Top Copper,COPPER_BOTTOM=Bottom Copper,DRILL=Drill,SILK_TOP=Silk Top,BOARD=Outline";
    /// <summary>The shipped two-layer board technology the DXF import's own tests read with: 0.508 mm of εr 3.66, 1 oz copper.</summary>
    private const string TechId = DxfBoardImportTests.TechId;

    private readonly string _root = Directory.CreateTempSubdirectory("crf-dxf-recognition-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Board(string name) => Path.Combine(RepoRoot(), "testdata", "interchange", "dxf-board", name);

    // ── the circuit the board is, by hand from make_boards.py ────────────────────────────────────────

    /// <summary>Each part and port, and what is on each of its two nodes: the designators and ports there (+), or 0.</summary>
    private static readonly (string Name, string A, string B)[] Parts =
    [
        ("P1", "P1", "0"), ("P2", "P2", "0"), ("P3", "P3", "0"), ("P4", "P4", "0"),
        ("C1", "C1", "C1"),                                  // across the input line's gap
        ("C2", "C2+C3", "0"), ("C3", "C2+C3", "0"),          // the device pad's two shunt parts: one node
        ("R1", "R1", "R1"), ("C4", "C4", "0"),               // the bias strip up
        ("R2", "R2", "R2"), ("C5", "C5", "0"),               // and its mirror image down
        ("C8", "C8", "C8"),                                  // into the second pad
        ("C6", "C6+C7", "0"), ("C7", "C6+C7", "0"),          // across the 0.80 and 0.51 mm gaps
    ];

    /// <summary>Each line: its type, width and length in mm (the copper's, gap edge, pad edge or tap to the next), and its
    /// two nodes as above.</summary>
    private static readonly (string Type, double W, double L, string A, string B)[] Lines =
    [
        ("CPWG", 1.1, 6.0, "P1", "C1"),
        ("CPWG", 1.1, 5.25, "C1", "C2+C3"),
        ("CPWG", 2.0, 5.25, "C2+C3", "R1"),
        ("CPWG", 2.0, 2.85, "R1", "C4"),                     // to C4, 3.1 mm on from R1's middle
        ("CPWG", 2.0, 4.15, "C4", "P2"),
        ("CPWG", 2.0, 5.25, "C2+C3", "R2"),
        ("CPWG", 2.0, 2.85, "R2", "C5"),
        ("CPWG", 2.0, 4.15, "C5", "P3"),
        ("CPWG", 1.1, 6.25, "C2+C3", "C8"),
        ("TLIN", 1.7, 5.2, "C8", "C6+C7"),                   // the 1.7 mm run: its gaps differ by more than 1.5×
        ("CPWG", 1.1, 8.8, "C6+C7", "P4"),                   // with the 0.4 mm of 1.7 mm past the taps
    ];

    /// <summary>The same circuit with the board turned over top to bottom: every designator moves with its part, and only the
    /// two bias ports change numbers, the top edge's being P2.</summary>
    private static string Mirrored(string s) =>
        string.Join("+", s.Split('+').Select(n => n switch { "P2" => "P3", "P3" => "P2", _ => n }).Order(StringComparer.Ordinal));

    // ── reading a drawing ────────────────────────────────────────────────────────────────────────

    /// <summary>The drawing imported as `convert --layer-map` imports it (DxfImport with the map applied to the dialog's
    /// rows), then recognised and emitted as Create Schematic from Artwork does.</summary>
    private (RecognitionResult Result, RecognitionCircuit Circuit) Recognise(string dxf, int? units = null, string map = Map)
    {
        var tech = ShippedTechnologies.Load(TechId);
        string into = Path.Combine(_root, Path.GetFileNameWithoutExtension(dxf));
        DxfImport.ImportResult imported;
        using (var stream = File.OpenRead(dxf))
            imported = DxfImport.Import(stream, into, tech, LayoutUnits.DefaultDbuPerMicron,
                resolveUnits: units is { } u ? _ => u : null,
                resolveMapping: request =>
                {
                    var (rows, refusal) = DxfBoardReading.ApplyMap(map, request.Rows, tech);
                    Assert.True(refusal is null, refusal);
                    return new DxfMappingAnswer(rows!);
                },
                importName: "board");
        Assert.False(imported.Cancelled, string.Join("\n", imported.Messages));
        var view = LayoutPersistence.LoadFromFile(
            Directory.GetFiles(CellFolder.SubFolderPath(Assert.Single(imported.CreatedCellDirs), ViewType.Layout), "*.clay").Single());
        var input = new RecognitionInput { View = view, Technology = tech, Shapes = view.Shapes };
        var result = ArtworkRecognition.Recognize(input);
        Assert.True(result.Ok, result.Refusal);
        return (result, RecognitionEmit.Build(result, input));
    }

    /// <summary>
    /// The circuit is the board's: every part found and named from its text, one node per pad, every line there at its
    /// width and length (±1 %), nothing more; the check of the circuit against its copper says they agree, no line end
    /// is open, and no terminal was joined across copper no line models.
    /// </summary>
    private static void AssertIsTheBoard(RecognitionResult result, RecognitionCircuit circuit, Func<string, string>? turn = null)
    {
        turn ??= s => s;
        var report = string.Join("\n", result.Report.Lines());
        Assert.True(result.Report.Count(RecognitionFindingClass.CircuitSplitsCopper) == 0, report);
        Assert.True(result.Report.Count(RecognitionFindingClass.CircuitJoinsAcrossCopper) == 0, report);
        Assert.True(result.Report.Count(RecognitionFindingClass.CopperJoinedWithoutLine) == 0, report);
        Assert.True(result.Report.Count(RecognitionFindingClass.OpenEnds) == 0, report);
        Assert.True(result.Report.Count(RecognitionFindingClass.CircuitAgreesWithCopper) > 0, report);
        Assert.Equal(10, result.Report.Count(RecognitionFindingClass.PartsFromBodyOutlines));
        Assert.Equal(10, result.Report.Count(RecognitionFindingClass.SilkscreenLabelsRead));

        var tb = circuit.TestBench;
        var terminals = tb.Instances.Where(i => i.Reference is "Port" or "C" or "R" or "L")
                          .SelectMany(i => i.NetBindings.Select(n => (Net: n, i.InstanceName))).ToLookup(x => x.Net, x => x.InstanceName);
        string On(string net) => net == "0" ? "0" : string.Join("+", terminals[net].Distinct().Order(StringComparer.Ordinal));
        string Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a} | {b}" : $"{b} | {a}";

        var parts = tb.Instances.Where(i => i.Reference is "Port" or "C" or "R" or "L")
                      .ToDictionary(i => i.InstanceName, i => Pair(On(i.NetBindings[0]), On(i.NetBindings[1])));
        var expected = Parts.ToDictionary(p => turn(p.Name), p => Pair(turn(p.A), turn(p.B)));
        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), parts.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.All(result.Parts.Rows, r => Assert.Equal(PartEvidenceSource.Silkscreen, r.Evidence[PartField.Refdes]));

        double Mm(Instance i, string name)
        {
            var o = i.Overrides.Single(p => p.Name == name);
            double scale = o.Unit switch { "mm" => 1, "um" or "µm" => 1e-3, "m" => 1e3, _ => double.NaN };
            return double.Parse(o.Expression, System.Globalization.CultureInfo.InvariantCulture) * scale;
        }
        var lines = tb.Instances.Where(i => i.Reference is "MLIN" or "CPWG" or "TLIN" or "SLIN").ToList();
        var left = lines.ToList();
        foreach (var (type, w, l, a, b) in Lines)
        {
            string pair = Pair(turn(a), turn(b));
            var match = left.FirstOrDefault(i => i.Reference == type && Pair(On(i.NetBindings[0]), On(i.NetBindings[1])) == pair
                                                 && Math.Abs(Mm(i, "L") - l) <= 0.01 * l
                                                 && Math.Abs((type == "TLIN" ? circuit.Measured[i.InstanceName]["W"] * 1e3 : Mm(i, "W")) - w) <= 0.001);
            Assert.True(match is not null,
                        $"no {type} W={w} L={l} mm between {pair}; the lines are:\n" +
                        string.Join("\n", lines.Select(i => $"{i.Reference} {Pair(On(i.NetBindings[0]), On(i.NetBindings[1]))} L={Mm(i, "L"):0.###}")) +
                        "\n" + report);
            left.Remove(match!);
        }
        Assert.Empty(left);
        Assert.DoesNotContain(tb.Instances, i => i.Reference is not ("Port" or "C" or "R" or "L" or "MLIN" or "CPWG" or "TLIN" or "SLIN"));
    }

    // ── the gates ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every spelling of the board is the same circuit (R12, R2000, inches with $INSUNITS, LINE and ARC strokes),
    /// and with no $INSUNITS the --dxf-units answer is what makes it so.</summary>
    [Theory]
    [InlineData("board-r12.dxf", null)]
    [InlineData("board-r2000.dxf", null)]
    [InlineData("board-inch.dxf", null)]
    [InlineData("board-strokes.dxf", null)]
    [InlineData("board-nounits.dxf", 1)]
    public void EverySpelling_IsTheSameCircuit(string file, int? units)
    {
        var (result, circuit) = Recognise(Board(file), units);
        AssertIsTheBoard(result, circuit);
    }

    /// <summary>Turned over top to bottom, the board is the same circuit, its two bias ports numbered the other way round: the
    /// two mirrored bias halves, and the two shunt parts either side of the 1.7 mm line, read alike either way up.</summary>
    [Fact]
    public void TheMirrorImage_IsTheSameCircuit_UpToWhichHalfIsWhich()
    {
        var (result, circuit) = Recognise(Board("board-mirror.dxf"));
        AssertIsTheBoard(result, circuit, Mirrored);
    }

    /// <summary>The board written back out by circuitRF's own DXF export (`convert --to dxf`) and imported again with a
    /// map of the technology's own layer names is the same circuit.</summary>
    [Fact]
    public void OurOwnExportOfTheBoard_ImportedByHand_IsTheSameCircuit()
    {
        var tech = ShippedTechnologies.Load(TechId);
        string first = Path.Combine(_root, "first");
        using (var stream = File.OpenRead(Board("board-r12.dxf")))
        {
            var imported = DxfImport.Import(stream, first, tech, LayoutUnits.DefaultDbuPerMicron, resolveMapping: request =>
            {
                var (rows, refusal) = DxfBoardReading.ApplyMap(Map, request.Rows, tech);
                Assert.Null(refusal);
                return new DxfMappingAnswer(rows!);
            }, importName: "board");
            DxfExport.Write(Path.Combine(_root, "ours.dxf"),
                            DxfExport.Analyze(Assert.Single(imported.CreatedCellDirs), tech, LayoutUnits.DefaultDbuPerMicron),
                            new DxfExportOptions(InsUnits: DxfUnits.Millimeters));
        }
        var (result, circuit) = Recognise(Path.Combine(_root, "ours.dxf"),
            map: "Top Copper=Top Copper,Bottom Copper=Bottom Copper,Drill=Drill,Silk Top=Silk Top,Outline=Outline");
        AssertIsTheBoard(result, circuit);
    }

    /// <summary>Headlessly end to end, as an agent runs it: convert with no --from, recognise to a .cnl, and check that netlist
    /// with no error.</summary>
    [Fact]
    public void TheCli_ConvertsRecognisesAndChecksTheBoard()
    {
        string ws = Path.Combine(_root, "ws");
        var (code, _, stderr) = CliProcess.Run(_root, [], "convert", Board("board-r2000.dxf"), "-o", ws, "--to", "clay",
                                               "--tech", TechId, "--layer-map", Map);
        Assert.True(code == 0, stderr);
        string clay = Directory.GetFiles(ws, "*.clay", SearchOption.AllDirectories).Single();
        string cnl = Path.Combine(_root, "board.cnl");
        var (rcode, rout, rerr) = CliProcess.Run(_root, [], "recognize", clay, "-o", cnl);
        Assert.True(rcode == 0, rout + rerr);
        Assert.Contains("The circuit joins what the copper joins", rout);
        var (ccode, cout, cerr) = CliProcess.Run(_root, [], "check", cnl);
        Assert.True(ccode == 0, cout + cerr);
        Assert.Contains("0 error", cout + cerr);
    }
}
