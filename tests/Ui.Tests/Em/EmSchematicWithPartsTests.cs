using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.SchematicSync;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-isc-1 — Create SnP schematic with Include pre-existing schematic components, and Create now. Every gate builds the cell from a
// fixed Touchstone and a port list (no solve); the designs are the schematic-driven layout set's F1 (Port – line – series C – line –
// Port) and F2 (a shunt C between two lines).
public sealed class EmSchematicWithPartsTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("isc1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    private static readonly double[] FreqsGHz = [1, 2, 3];

    private static EmSetup Setup() => new()
    {
        Name = "board", CreateSnpSchematic = true, IncludeSchematicComponents = true,
        Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
    };

    private static EmCoSimPart Part(string refdes) => new(refdes, "1", "2", ["1", "2"], refdes, null, (null, null), false);

    /// <summary>Gate 1 — every part on a component port and the Ports numbered in result order: the cell made from F1's schematic
    /// simulates to the co-simulation netlist's S-parameters, entry by entry.</summary>
    [Fact]
    public void TheCell_IsTheSameCircuitAsTheCoSimNetlist()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        string snp = Touchstone(p.Workspace, 3);
        EmCoSimPort[] ports = [new(1, "1", "50", null), new(2, "2", "50", null), new(3, "C1", "50", Part("C1"))];

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, ports, new Dictionary<int, EmPortFace>(), out var fallback, checkLvs: false);
        Assert.Null(fallback);
        Assert.Equal(EmSnpSchematicOutcome.Created, made!.Outcome);
        Assert.Equal("board_sch", made.CellName);
        Assert.Empty(made.Warnings);

        string cnl = Path.Combine(p.Workspace, "board_cosim.cnl");
        File.WriteAllText(cnl, EmCoSimNetlist.Text(Setup(), "results/board.s3p", ports, EmCoSimNetlist.ModelsForCell(p.CellDir)));
        Assert.Contains("C:C1", File.ReadAllText(cnl), StringComparison.Ordinal);
        var expected = SchematicRunService.RunNetlist(cnl, p.Workspace);
        Assert.True(expected.Status == RunStatus.Success, expected.StatusMessage);

        AssertSame(expected.DataSets[0]["S"], Simulate(p.Workspace, made.SchematicPath!)["S"], 2);
    }

    /// <summary>Gate 2 — ports are matched by number: the schematic's Port 1 (left) joins the EM port the document numbers 1, which is
    /// the Touchstone's second. S11 of the cell is the file's S22. F1's C1 has a footprint and no component port, so it is left open
    /// and named.</summary>
    [Fact]
    public void Ports_AreMatchedByNumber_NotByOrder()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        string snp = Touchstone(p.Workspace, 2);
        EmCoSimPort[] ports = [new(1, "2", "50", null, DocumentNumber: 2), new(2, "1", "50", null, DocumentNumber: 1)];

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, ports, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        Assert.Contains(made.Warnings, w => w.StartsWith("C1 has a footprint but no component port", StringComparison.Ordinal));

        var s = Simulate(p.Workspace, made.SchematicPath!)["S"];
        var file = RfCore.TouchstoneIO.ReadFile(snp);
        for (int f = 0; f < FreqsGHz.Length; f++)
        {
            var s11 = s.At("freq", f).At("i", 0).At("j", 0).ComplexValues[0];
            Assert.True((s11 - file.Matrices[f][1, 1]).Magnitude < 1e-9, $"S11 at {FreqsGHz[f]} GHz: {s11} against S22 {file.Matrices[f][1, 1]}");
        }
    }

    /// <summary>Gate 3 — the sheet is reused: every kept component where it was, with its parameters; the copper gone; an analysis set
    /// to run by EM comes back by circuit models; and the user's schematic is not touched.</summary>
    [Fact]
    public void TheSheet_IsTheUsersSheet()
    {
        var p = SdlChecker.Prepare("F2-shunt-cap", _scratch);
        p.Model.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")]) { Model = AnalysisModel.Em });
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        string before = File.ReadAllText(p.SchematicPath);
        string snp = Touchstone(p.Workspace, 3);
        EmCoSimPort[] ports = [new(1, "1", "50", null), new(2, "2", "50", null), new(3, "C1", "50", Part("C1"))];

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, ports, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        var (copy, _, _) = SchematicPersistence.LoadFromFile(made.SchematicPath!);

        foreach (string name in new[] { "C1", "P1", "P2" })
        {
            var a = p.Model.Components.Single(c => c.InstanceName == name);
            var b = copy.Components.Single(c => c.InstanceName == name);
            Assert.Equal((a.X, a.Y, a.Rotation, a.MirrorX), (b.X, b.Y, b.Rotation, b.MirrorX));
            Assert.Equal(a.Parameters.Select(q => (q.Name, q.Expression, q.Unit)), b.Parameters.Select(q => (q.Name, q.Expression, q.Unit)));
        }
        Assert.DoesNotContain(copy.Components, c => c.Symbol == SymbolKind.Mlin);
        Assert.Single(copy.Components, c => c.Symbol == SymbolKind.Snp);
        Assert.Equal(AnalysisModel.Circuit, Assert.Single(copy.Analyses).Model);
        Assert.Equal(before, File.ReadAllText(p.SchematicPath));
        Simulate(p.Workspace, made.SchematicPath!);
    }

    /// <summary>Gate 4 — a part that is not on the board keeps its own wiring; one joined only to removed copper is kept with that
    /// pin open, and named.</summary>
    [Fact]
    public void OffBoardParts_AreKept_AndAnOrphanedPinIsNamed()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        var tl1 = p.Model.Components.Single(c => c.InstanceName == "TL1");
        var (tx, ty) = tl1.GetPortWorldCoord(1);
        // R9 on TL1's far end, pin to pin: TL1 is copper, so R9's pin has nothing left to join.
        var r9 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R9", X = tx, Y = ty - 200 };
        r9.Parameters.Add(new EditableParameter { Name = "R", Expression = "10", Unit = "Ω" });
        p.Model.Components.Add(r9);
        Assert.Equal((tx, ty), r9.GetPortWorldCoord(1));
        // R7 and R8 joined by a wire, far from everything: not on the board, and nothing of theirs is removed.
        var r7 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R7", X = 0, Y = 3000 };
        var r8 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R8", X = 1000, Y = 3000 };
        p.Model.Components.AddRange([r7, r8]);
        var wire = new EditableWire();
        wire.Points.Add(r7.GetPortWorldCoord(1));
        wire.Points.Add((r7.GetPortWorldCoord(1).X, r8.GetPortWorldCoord(1).Y + 0));
        wire.Points.Add(r8.GetPortWorldCoord(1));
        p.Model.Wires.Add(wire);
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        string snp = Touchstone(p.Workspace, 3);
        EmCoSimPort[] ports = [new(1, "1", "50", null), new(2, "2", "50", null), new(3, "C1", "50", Part("C1"))];

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, ports, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        Assert.Contains(made.Warnings, w => w.StartsWith("R9 is not on the board", StringComparison.Ordinal));
        Assert.DoesNotContain(made.Warnings, w => w.StartsWith("R7", StringComparison.Ordinal) || w.StartsWith("R8", StringComparison.Ordinal));
        var (copy, _, _) = SchematicPersistence.LoadFromFile(made.SchematicPath!);
        Assert.Contains(copy.Wires, w => w.Points.SequenceEqual(wire.Points));
        Assert.Contains(copy.Components, c => c.InstanceName == "R9");
    }

    /// <summary>
    /// The shipped PCB Component Ports board: Pins (not Ports) mark its board ports, and three parts are close enough that a stub drawn
    /// straight out of each pin ends on another's wire — which is a connection, and shorted C2's node to ground before stubs were
    /// placed against the sheet. Each part must extract on its own SnP node and ground, the Pins on theirs, and the cell gets the
    /// original's symbol so it can stand where the original is placed.
    /// </summary>
    [Fact]
    public void CloseParts_EachGetTheirOwnNode_AndAPinCellKeepsItsSymbol()
    {
        string repo = Path.GetFullPath(Path.Combine(SdlChecker.DesignsRoot, "..", ".."));
        // The example's whole workspace: its technology is what the board's lines extract against.
        string ws = Path.Combine(_scratch, "ws");
        string source = Path.Combine(repo, "examples", "PCB Component Ports");
        foreach (string f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(ws, Path.GetRelativePath(source, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        string cellDir = Path.Combine(ws, "Filter Board");
        string snp = Touchstone(ws, 5);
        EmCoSimPort[] ports = [new(1, "P1", "50", null), new(2, "P2", "50", null),
                               new(3, "C1", "50", Part("C1")), new(4, "L1", "50", Part("L1")), new(5, "C2", "50", Part("C2"))];

        var made = EmSchematicWithParts.Create(ws, Setup(), cellDir, snp, ports, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        Assert.Empty(made.Warnings);
        Assert.Contains("Its Pins are the board's ports", made.Sentence, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(ws, made.CellName, "symbol"), "*.csym"));

        var (model, _, cell) = SchematicPersistence.LoadFromFile(made.SchematicPath!);
        var tb = NetExtractor.Extract(model, cell).TestBench;
        IReadOnlyList<string> Nets(string name) => tb.Instances.Single(i => i.InstanceName == name).NetBindings;
        var em = Nets(EmSchematicWithParts.SnpInstanceName);
        Assert.Equal(5, em.Distinct().Count());
        Assert.DoesNotContain("0", em);
        Assert.Equal([em[2], "0"], Nets("C1"));
        Assert.Equal([em[3], "0"], Nets("L1"));
        Assert.Equal([em[4], "0"], Nets("C2"));
        Assert.Empty(model.Analyses);
    }

    /// <summary>
    /// Two parts drawn pin to pin, with no wire between them, each on its own component port: the touching pins are one point, so
    /// a stub from it joined both SnP nodes and the second part extracted on the first part's node. One is moved clear, and each
    /// stands on its own node and ground.
    /// </summary>
    [Fact]
    public void PartsDrawnPinToPin_EachGetTheirOwnNode()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        var r7 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R7", X = 0, Y = 3000, Rotation = SymbolRotation.R90 };
        var r8 = new EditableComponent { Symbol = SymbolKind.Resistor, InstanceName = "R8", X = 0, Y = 3000, Rotation = SymbolRotation.R90 };
        foreach (var r in new[] { r7, r8 }) r.Parameters.Add(new EditableParameter { Name = "R", Expression = "10", Unit = "Ω" });
        var (ax, ay) = r7.GetPortWorldCoord(1);
        var (bx, by) = r8.GetPortWorldCoord(0);
        r8.X += ax - bx; r8.Y += ay - by;
        Assert.Equal(r7.GetPortWorldCoord(1), r8.GetPortWorldCoord(0));
        p.Model.Components.AddRange([r7, r8]);
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        string snp = Touchstone(p.Workspace, 4);
        EmCoSimPort[] ports = [new(1, "1", "50", null), new(2, "2", "50", null), new(3, "R7", "50", Part("R7")), new(4, "R8", "50", Part("R8"))];

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, ports, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        Assert.DoesNotContain(made.Warnings, w => w.Contains("pin to pin", StringComparison.Ordinal));
        var (model, _, cell) = SchematicPersistence.LoadFromFile(made.SchematicPath!);
        var tb = NetExtractor.Extract(model, cell).TestBench;
        IReadOnlyList<string> Nets(string name) => tb.Instances.Single(i => i.InstanceName == name).NetBindings;
        var em = Nets(EmSchematicWithParts.SnpInstanceName);
        Assert.Equal(4, em.Distinct().Count());
        Assert.Equal([em[2], "0"], Nets("R7"));
        Assert.Equal([em[3], "0"], Nets("R8"));
    }

    /// <summary>Gate 5 — with no Primary schematic the caller is told to make the ordinary cell, naming the cell it looked in; a second
    /// making leaves the first cell as it is.</summary>
    [Fact]
    public void NoPrimarySchematic_FallsBack_AndAnExistingCellIsLeftAlone()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        string snp = Touchstone(p.Workspace, 2);
        string empty = Directory.CreateDirectory(Path.Combine(p.Workspace, "Empty")).FullName;
        Assert.Null(EmSchematicWithParts.Create(p.Workspace, Setup(), empty, snp, null, new Dictionary<int, EmPortFace>(), out var fallback, checkLvs: false));
        Assert.Contains("no Primary schematic in cell Empty", fallback, StringComparison.Ordinal);

        var first = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, null, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        string bytes = File.ReadAllText(first.SchematicPath!);
        var second = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, null, new Dictionary<int, EmPortFace>(), out _, checkLvs: false)!;
        Assert.Equal(EmSnpSchematicOutcome.Current, second.Outcome);
        Assert.Equal(bytes, File.ReadAllText(first.SchematicPath!));
    }

    /// <summary>Gate 7 — a layout that differs from its schematic: the cell is still made, and the result says so.</summary>
    [Fact]
    public void AnLvsDifference_IsAWarning_NotARefusal()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        LayoutPersistence.SaveToFile(p.ClayPath, p.Applied(p.Plan()));
        p.Model.Components.Single(c => c.InstanceName == "C1").InstanceName = "C5";
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        string snp = Touchstone(p.Workspace, 2);

        var made = EmSchematicWithParts.Create(p.Workspace, Setup(), p.CellDir, snp, null, new Dictionary<int, EmPortFace>(), out _)!;
        Assert.Equal(EmSnpSchematicOutcome.Created, made.Outcome);
        Assert.Contains(made.Warnings, w => w.StartsWith("The layout and the schematic differ", StringComparison.Ordinal));
    }

    /// <summary>Gate 8 — the flag is written only when on, and stripped from a 3D run's record with Create SnP schematic.</summary>
    [Fact]
    public void TheFlag_IsOmittedWhenOff_AndRoundTrips()
    {
        var off = new EmSetup { Name = "s" };
        Assert.DoesNotContain("IncludeSchematicComponents", EmSetupPersistence.Serialize(off), StringComparison.Ordinal);
        var on = new EmSetup { Name = "s", IncludeSchematicComponents = true };
        string text = EmSetupPersistence.Serialize(on);
        Assert.Contains("\"IncludeSchematicComponents\": true", text, StringComparison.Ordinal);
        Assert.True(EmSetupPersistence.Deserialize(text).IncludeSchematicComponents);
        Assert.True(on.Clone().IncludeSchematicComponents);
    }

    /// <summary>Create now with no Touchstone yet: the cell is made, its block names the file the run will write, and it warns. A second
    /// Create now with Replace writes over it; with a new name it makes another cell.</summary>
    [Fact]
    public void CreateNow_WithNoTouchstone_MakesTheCell_AndReplaceOrNewNameAnswerAnExistingOne()
    {
        string ws = Directory.CreateDirectory(Path.Combine(_scratch, "ws")).FullName;
        var setup = new EmSetup { Name = "board", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };
        string missing = EmRunService.ResolveSnpPath(Path.Combine(ws, "results"), setup, 2);
        EmCoSimPort[] ports = [new(1, "1", "50", null), new(2, "2", "50", null)];

        var made = EmSnpSchematic.Create(ws, setup, missing, new Dictionary<int, EmPortFace>(), ports);
        Assert.Equal(EmSnpSchematicOutcome.Created, made.Outcome);
        Assert.Contains(made.Warnings, w => w.StartsWith("No Touchstone exists yet at results/board.s2p", StringComparison.Ordinal));
        Assert.Contains("results/board.s2p", File.ReadAllText(made.SchematicPath!), StringComparison.Ordinal);

        var replaced = EmSnpSchematic.Create(ws, setup, missing, new Dictionary<int, EmPortFace>(), ports, replace: true);
        Assert.StartsWith("Replaced cell board_sim", replaced.Sentence, StringComparison.Ordinal);
        Assert.Equal(made.SchematicPath, replaced.SchematicPath);

        var renamed = EmSnpSchematic.Create(ws, setup, missing, new Dictionary<int, EmPortFace>(), ports, cellName: "board_sim_2");
        Assert.Equal("board_sim_2", renamed.CellName);
        Assert.True(File.Exists(renamed.SchematicPath));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The schematic run as Simulate runs it: extracted, written to the workspace root, run.</summary>
    private static RfCore.Data.DataSet Simulate(string workspace, string schematicPath)
    {
        var (model, _, cell) = SchematicPersistence.LoadFromFile(schematicPath);
        var tb = NetExtractor.Extract(model, cell);
        Assert.Empty(tb.Conflicts);
        string cnl = Path.Combine(workspace, "netlist.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(tb.TestBench, "; isc-1"));
        var run = SchematicRunService.RunNetlist(cnl, workspace);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        return run.DataSets[0];
    }

    private static void AssertSame(RfCore.Data.DataCube expected, RfCore.Data.DataCube got, int ports)
    {
        for (int f = 0; f < FreqsGHz.Length; f++)
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                {
                    var e = expected.At("freq", f).At("i", i).At("j", j).ComplexValues[0];
                    var g = got.At("freq", f).At("i", i).At("j", j).ComplexValues[0];
                    Assert.True((g - e).Magnitude < 1e-9, $"S({i + 1},{j + 1}) at {FreqsGHz[f]} GHz: {g} against {e}");
                }
    }

    /// <summary>A reciprocal Touchstone of <paramref name="n"/> ports at <see cref="FreqsGHz"/>, at <c>results/board.sNp</c>, no two
    /// entries alike so a swapped port shows.</summary>
    private static string Touchstone(string workspace, int n)
    {
        string path = Path.Combine(workspace, "results", $"board.s{n}p");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder("# GHz S RI R 50\n");
        for (int f = 0; f < FreqsGHz.Length; f++)
        {
            sb.Append(FreqsGHz[f].ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (j > 0 && j % 4 == 0) sb.Append('\n');
                    int a = Math.Min(i, j), b = Math.Max(i, j);
                    var v = Complex.FromPolarCoordinates(a == b ? 0.15 + 0.1 * a : 0.3 + 0.05 * (a + b), 0.4 * a + 0.9 * b + 0.3 * f);
                    sb.Append(' ').Append(v.Real.ToString("R", CultureInfo.InvariantCulture))
                      .Append(' ').Append(v.Imaginary.ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
