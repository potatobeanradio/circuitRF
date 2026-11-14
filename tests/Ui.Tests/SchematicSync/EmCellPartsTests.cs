using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-icp-8 — an EM cell whose schematic places parts that are not copper. The design is the schematic-driven layout set's F7
/// (two lines and a three-terminal part inline, its third pin on ground) with the part made a device sub-cell, Dev: a tee of
/// two resistors and a capacitor whose value is the schematic's VAR <c>cm</c>, laid out as F7's three-pad package. Its schematic is a
/// model, so Q1 is a device (ICP-0 D3); the EM view names it, in Cell mode, so the solve cuts it out at its cell edge with an
/// Uncalibrated Port where each conductor crosses it (ICP-2), and the bench puts Dev back on them from the schematic being run.
/// </summary>
public sealed class EmCellPartsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-icp8-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Gate 1 — model=em on the schematic matches the EM view's own run of the same layout closed by the co-simulation netlist that
    /// run writes, entry by entry, to 1e-9: the EM cell builds the same cut and puts the same model back on it.
    /// </summary>
    /// <remarks>Two planar solves, ~30 s together: the Benchmark tier (gates 2 and 3 are routine and solve nothing).</remarks>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ModelEm_MatchesTheSetupsRunClosedByItsCoSimNetlist()
    {
        var fx = Fixture("gate1", AnalysisModel.Em);

        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest { EmMaxSolves = 1 });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var em = run.Results.Single(r => r.Name == "SP1").Data["S"];

        var source = Source(fx);
        var setup = EmSetupPersistence.LoadFromFile(fx.Cem);
        var cem = EmRunService.Run(setup, source, Path.Combine(fx.Workspace, "results"));
        Assert.True(cem.Status == EmRunStatus.Ok, cem.Error);
        Assert.Contains("! Port[3] = Q1.", File.ReadAllText(cem.SnpPath!), StringComparison.Ordinal);
        Assert.NotNull(cem.CoSim?.Path);
        var closed = SchematicRunService.RunNetlist(cem.CoSim!.Path!, fx.Workspace);
        Assert.True(closed.Status == RunStatus.Success, closed.StatusMessage);
        var expected = closed.DataSets[0]["S"];

        double worst = 0;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                worst = Math.Max(worst, (At(em, 0, i, j) - At(expected, 0, i, j)).Magnitude);
        output.WriteLine($"max |ΔS| model=em vs the .cem run closed by its netlist = {worst:E2}");
        Assert.True(worst < 1e-9, $"max |ΔS| {worst}");
        // The device is in the circuit: with Q1 open the line would pass nothing.
        Assert.True(At(em, 0, 1, 0).Magnitude > 0.05, $"|S21| {At(em, 0, 1, 0).Magnitude}");
    }

    /// <summary>
    /// Gate 2 — a sweep of the device's value re-evaluates the bench at each point and solves once: the counting pass sees two
    /// evaluations of one geometry, and a run solves one geometry and gives two different answers.
    /// </summary>
    [Fact]
    public void ADeviceValue_ReEvaluatesWithNoNewSolve()
    {
        var sweep = new ParametricSweepAnalysis("SW1", "cm", [1e-12, 4e-12], "SP1");
        var fx = Fixture("gate2", AnalysisModel.Em, sweep);

        var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest());
        var count = CircuitEvaluation.CountEmSolves(plan);
        Assert.Empty(count.Refusals);
        Assert.Equal((2, 1, 1), (count.Evaluations, count.Geometries, count.ToSolve));

        int solves = 0;
        EmEvaluation.SolverStandIn = (g, _, path) =>
        {
            Interlocked.Increment(ref solves);
            string snp = path + ".s5p";
            File.WriteAllText(snp, FivePort());
            return snp;
        };
        try
        {
            var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest());
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
            var s = run.Results.Single(r => r.Name == "SP1").Data["S"];
            Assert.Equal(1, solves);
            var a = s.At("cm", 0).At("freq", 0).At("i", 1).At("j", 0).ComplexValues[0];
            var b = s.At("cm", 1).At("freq", 0).At("i", 1).At("j", 0).ComplexValues[0];
            output.WriteLine($"S21 at cm = 1 pF {a}, at 4 pF {b}");
            Assert.True((a - b).Magnitude > 1e-3, $"S21 {a} against {b}: the device's value did not reach the bench");
        }
        finally { EmEvaluation.SolverStandIn = null; }
    }

    /// <summary>Gate 3 — a device whose port cannot be built refuses the analysis, naming it and the builder's reason.</summary>
    [Fact]
    public void ADeviceWhosePortCannotBeBuilt_Refuses_NamingIt()
    {
        // Q1's ports referenced to a pin it does not have: the builder refuses them.
        var fx = Fixture("gate3", AnalysisModel.Em, cem: s => s.ComponentPorts = [new EmComponentPort("Q1") { PortAt = EmComponentPortAt.Cell, Reference = "nowhere" }]);
        var count = CircuitEvaluation.CountEmSolves(CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest()));
        string refusal = Assert.Single(count.Refusals);
        output.WriteLine(refusal);
        Assert.Contains("Q1", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// ICP-0 D3, as brief-icp-9 reads it: a sub-cell whose schematic is a model (a part with no layout of its own) is a device,
    /// which <c>"*"</c> takes and an EM cell puts on component ports by itself; a sub-cell drawn from lines stays copper.
    /// </summary>
    [Fact]
    public void AModelCell_IsAPart_ALineCell_IsCopper()
    {
        string ws = Path.Combine(_scratch, "kinds");
        Directory.CreateDirectory(Path.Combine(ws, "tech"));
        Dev(ws, Path.Combine(ws, "tech", "microstrip-0508.ctech"));
        string line = CellFolder.CreateCellFolder(ws, "Line");
        Directory.CreateDirectory(CellFolder.SubFolderPath(line, ViewType.Layout));
        Directory.CreateDirectory(CellFolder.SubFolderPath(line, ViewType.Schematic));
        File.Copy(Path.Combine(CellFolder.SubFolderPath(Path.Combine(ws, "Dev"), ViewType.Layout), "Dev.clay"),
                  Path.Combine(CellFolder.SubFolderPath(line, ViewType.Layout), "Line.clay"));
        var sch = new SchematicEditModel();
        sch.Components.Add(Pin(1, "A", -400, 0));                                        // port at (-300, 0)
        sch.Components.Add(Pin(2, "B", 300, 0));                                         // port at (400, 0)
        sch.Components.Add(Part("TL1", SymbolKind.Mlin, 0, 0, "L", "1", "mm"));            // (-200,0)-(200,0)
        sch.Wires.Add(Wire((-300, 0), (-200, 0)));
        sch.Wires.Add(Wire((200, 0), (400, 0)));
        SchematicPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(line, ViewType.Schematic), "Line.csch"), sch);

        string here = Path.Combine(ws, "Top", "schematic");
        Directory.CreateDirectory(here);
        var device = new EditableComponent { InstanceName = "Q1", Symbol = SymbolKind.BjtNpn, CellRef = "../../Dev" };
        var copper = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Resistor, CellRef = "../../Line" };
        Assert.Equal(EmPartKind.Part, EmPartKinds.Of(device, here));
        Assert.Equal(EmPartKind.Copper, EmPartKinds.Of(copper, here));
    }

    /// <summary>
    /// brief-icp-10 — a sub-block whose schematic places a kit part with no layout view of its own (Q2, a model cell) is copper when
    /// its own layout PLACES that part (an instance carrying Q2's SchematicId, as a kit's PCell is placed), so <c>"*"</c> walks into it
    /// and takes the device inside; with no such placement it is still a model, a device. An open SiGe kit's cascode, a transistor
    /// inside a sub-cell, was cut whole and dropped before this.
    /// </summary>
    [Fact]
    public void ASubBlockWhoseLayoutPlacesItsKitPart_IsCopper()
    {
        string ws = Path.Combine(_scratch, "walk");
        string kit = CellFolder.CreateCellFolder(ws, "Kit");            // a model cell: schematic only, an ideal R inside
        var model = new SchematicEditModel();
        model.Components.Add(Part("R1", SymbolKind.Resistor, 0, 0, "R", "50", "Ohm"));
        SaveSchematic(kit, "Kit", model);

        string block = CellFolder.CreateCellFolder(ws, "Block");
        var sch = new SchematicEditModel();
        sch.Components.Add(new EditableComponent { InstanceName = "Q2", Symbol = SymbolKind.Generic, CellRef = "../../Kit" });
        SaveSchematic(block, "Block", sch);
        void Layout(string? schematicId)
            => CellCreate.WriteLayoutView(block, "Block", new LayoutView
            {
                Instances = { new LayoutInstance { CellRef = "../../.generated-cells/dev_0", Mag = 1.0, SchematicId = schematicId } },
            });

        string here = Path.Combine(ws, "Top", "schematic");
        Directory.CreateDirectory(here);
        var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../Block" };
        Layout("Q2");
        Assert.Equal(EmPartKind.Copper, EmPartKinds.Of(x1, here));
        Layout(null);
        File.SetLastWriteTimeUtc(Directory.GetFiles(block, "*.clay", SearchOption.AllDirectories).Single(), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(EmPartKind.Part, EmPartKinds.Of(x1, here));

        static void SaveSchematic(string cellDir, string name, SchematicEditModel m)
        {
            string dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            Directory.CreateDirectory(dir);
            SchematicPersistence.SaveToFile(Path.Combine(dir, name + ".csch"), m);
        }
    }

    /// <summary>brief-icp-9 — <c>"*"</c> cuts the model cell out, and the cut says it was chosen by <c>"*"</c> (every cut read
    /// "by name": the selection's entry is rewritten to the instance's own path before the label was taken from it).</summary>
    [Fact]
    public void Star_CutsTheModelCell_AndSaysSo()
    {
        var fx = Fixture("star", AnalysisModel.Em, cem: s => s.ComponentPorts = [new EmComponentPort("*") { PortAt = EmComponentPortAt.Cell }]);
        var build = EmPlanarComponentPorts.Build(EmSetupPersistence.LoadFromFile(fx.Cem), Source(fx))!;
        var q1 = build.Ports.Where(p => p.Cutout && p.Refdes == "Q1").ToList();
        Assert.NotEmpty(q1);
        Assert.All(q1, p => Assert.Equal("by \"*\"", p.CutPart!.Selected));
    }

    /// <summary>brief-icp-9 — a drawn port standing on metal a cut removes is refused naming the instance (port extraction said only
    /// that the port was on no conductor).</summary>
    [Fact]
    public void ADrawnPortOnACutInstance_IsRefused_NamingIt()
    {
        var fx = Fixture("oncut", AnalysisModel.Em);
        var source = Source(fx);
        var setup = EmSetupPersistence.LoadFromFile(fx.Cem);
        var region = EmPlanarComponentPorts.Build(setup, source)!.Cut!.Regions["Q1"].Outline[0].Xy;
        var port = source.View.Shapes.OfType<LabelShape>().First(l => l.IsPort);
        (port.X, port.Y) = ((region.Where((_, k) => k % 2 == 0).Min() + region.Where((_, k) => k % 2 == 0).Max()) / 2,
                            (region.Where((_, k) => k % 2 == 1).Min() + region.Where((_, k) => k % 2 == 1).Max()) / 2);
        var collected = EmComponentPorts.Collect(new EmLayoutSource(source.AbsolutePath, source.View, source.Technology, source.DbuPerMicron), setup);
        Assert.Contains($"The port label '{port.Text}'", collected.Refusal);
        Assert.Contains("stands on Q1, which this setup cuts out", collected.Refusal);
    }

    /// <summary>
    /// brief-icp-9 — a run whose cut device is a cell of the workspace makes its <c>_cosim</c> cell: the drawing places that
    /// cell's instance (it refused any netlist defining a cell), and a MIM capacitor drawn with its process parameters
    /// stated extracts with each stated once (the technology's copy was appended beside them).
    /// </summary>
    [Fact]
    public void ACoSimCell_PlacesTheDeviceCell_AndStatesEachProcessParameterOnce()
    {
        var ws = CircuitRF.Design.Workspace.WorkspaceCreate.Create(_scratch, "cosim", "mmic-GaAs_2LM_100um");
        Dev(ws.WorkspaceDir, ws.TechPath!);
        string text = """
            define Dev (C B E)
              R:R1  C  n1  R=20 Ohm
              R:R2  B  n1  R=30 Ohm
              C:C1  n1  E  C=1 pF
            end Dev

            SnP:BOARD  p1  p2  p3  p4  p5  p6  NumPorts=6  File="results/chip.s6p"
            Port:P1  p1  0  Num=1  Z=50 Ohm
            Port:P2  p2  0  Num=2  Z=50 Ohm
            Dev:Q1  p3  p4  0
            MIMCAP:C2  p5  p6  W=60 um  L=60 um  Er=6.8  Td=2E-07  TanD=0.001  SigmaTop=41000000  Ttop=2.5E-07  SigmaBot=41000000  Tbot=3E-06  H=0.0001  ErSub=12.9

            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            """;
        var setup = new EmSetup { Name = "chip", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };
        var made = EmCoSimNetlist.CreateCell(ws.WorkspaceDir, setup, text, Path.Combine(ws.WorkspaceDir, "results", "chip.s6p"), null);
        Assert.True(made.Outcome == EmSnpSchematicOutcome.Created, made.Sentence);

        string cnl = SchematicCircuit.CnlTextOf(made.SchematicPath!);
        output.WriteLine(cnl);
        Assert.Contains("Dev:Q1  p3  p4  0", cnl, StringComparison.Ordinal);
        string mim = cnl.Split('\n').Single(l => l.StartsWith("MIMCAP:C2", StringComparison.Ordinal));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(mim, @"\bEr=").Count);
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────────────────────────────

    private EmCellFixture Fixture(string tag, AnalysisModel model, ParametricSweepAnalysis? sweep = null, Action<EmSetup>? cem = null)
    {
        var p = SdlChecker.Prepare("F7-three-pad", Path.Combine(_scratch, tag));
        Dev(p.Workspace, Path.Combine(p.Workspace, "tech", "microstrip-0508.ctech"));
        var q1 = p.Model.Components.Single(c => c.InstanceName == "Q1");
        q1.Parameters.RemoveAll(x => x.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase));
        q1.CellRef = "../../Dev";
        var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 3000, Y = 1600 };
        vars.Parameters.Add(new EditableParameter { Name = "cm", Expression = "1", Unit = "pF" });
        p.Model.Components.Add(vars);
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);

        // Planned as an evaluation plans it: with the disk's cells, so the sub-cell has its nets.
        p.Model.SchematicDirectory = p.SchematicDir;
        var plan = SchematicLayoutPlanner.Plan(p.Model, p.Target, p.SchematicDir, p.Workspace, p.LayoutDir, p.Tech, p.TechPath,
                                                DiskCellResolver.Instance, targetLayoutPath: p.ClayPath, connectParts: true);
        var layout = p.Applied(plan);
        // The drawn ports uncalibrated (ICP-2a), as the cut ports are: no calibration standard is solved, so the solves stay short.
        foreach (var l in layout.Shapes.OfType<LabelShape>().Where(l => l.IsPort)) l.PortUncalibrated = true;
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        var created = EmSetupCreate.Create(p.Workspace, "F7", EmSetupCreate.LayoutRefOf(p.Workspace, p.ClayPath), s =>
        {
            s.AnalysisKind = EmAnalysisKind.Planar;
            s.Frequency = new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz");
            s.PlanarMesh = s.PlanarMesh with { CellsPerWavelength = 10 };
            // Cell mode: E's ground tie starts inside its pad, so the cut is at the cell's edge (pin mode refuses it, by design).
            s.ComponentPorts = [new EmComponentPort("Q1") { PortAt = EmComponentPortAt.Cell }];
            cem?.Invoke(s);
        });
        p.Model.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz")]) { Model = model });
        if (sweep is not null) p.Model.Analyses.Add(sweep);
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model);
        return new EmCellFixture(p, created.Path!);
    }

    /// <summary>Dev: pins C, B, E (the BJT symbol's order), a tee R1 (C) 20 Ω, R2 (B) 30 Ω, C1 = cm to E; its layout F7's PKG3.</summary>
    private static void Dev(string workspace, string techPath)
    {
        string dir = CellFolder.CreateCellFolder(workspace, "Dev");
        string layoutDir = CellFolder.SubFolderPath(dir, ViewType.Layout);
        string schematicDir = CellFolder.SubFolderPath(dir, ViewType.Schematic);
        Directory.CreateDirectory(layoutDir);
        Directory.CreateDirectory(schematicDir);

        var pkg = LayoutPersistence.LoadFromFile(Path.Combine(SdlChecker.DesignsRoot, "F7-three-pad", "parts", "PKG3", "layout", "PKG3.clay"));
        pkg.TechRef = Path.GetRelativePath(layoutDir, techPath).Replace('\\', '/');
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "Dev.clay"), pkg);

        // Its symbol: the BJT's pins where F7's wires end (C top, B left, E bottom), so the schematic's wiring is unchanged.
        string symbolDir = CellFolder.SubFolderPath(dir, ViewType.Symbol);
        Directory.CreateDirectory(symbolDir);
        File.WriteAllText(Path.Combine(symbolDir, "Dev.csym"), """
            {
              "FormatVersion": 6, "GridSize": 100, "PortCount": 3,
              "Primitives": [ { "$type": "Rect", "ColorRole": "SymbolLine", "StrokeTier": "Normal", "Filled": false, "Cx": 0.0, "Cy": 0.0, "W": 200.0, "H": 200.0 } ],
              "Pins": [
                { "LocalX": 0.0, "LocalY": -200.0, "PortIndex": 0, "Name": "C" },
                { "LocalX": -200.0, "LocalY": 0.0, "PortIndex": 1, "Name": "B" },
                { "LocalX": 0.0, "LocalY": 200.0, "PortIndex": 2, "Name": "E" }
              ]
            }
            """);

        var sch = new SchematicEditModel();
        sch.Components.Add(Pin(1, "C", -100, -400));             // port at (0, -400)
        sch.Components.Add(Pin(2, "B", -1100, -400));            // port at (-1000, -400)
        sch.Components.Add(Pin(3, "E", -100, 1200));             // port at (0, 1200)
        sch.Components.Add(Part("R1", SymbolKind.Resistor, 0, 200, "R", "20", "Ohm"));        // (0,0)-(0,400)
        sch.Components.Add(Part("R2", SymbolKind.Resistor, -1000, 200, "R", "30", "Ohm"));    // (-1000,0)-(-1000,400)
        sch.Components.Add(Part("C1", SymbolKind.Capacitor, 0, 600, "C", "cm", "pF"));        // (0,400)-(0,800)
        sch.Wires.Add(Wire((0, -400), (0, 0)));
        sch.Wires.Add(Wire((-1000, -400), (-1000, 0)));
        sch.Wires.Add(Wire((-1000, 400), (0, 400)));
        sch.Wires.Add(Wire((0, 800), (0, 1200)));
        SchematicPersistence.SaveToFile(Path.Combine(schematicDir, "Dev.csch"), sch);
    }

    private static EditableComponent Pin(int num, string name, double x, double y)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        c.Parameters.Add(new EditableParameter { Name = "Name", Expression = name });
        return c;
    }

    private static EditableComponent Part(string name, SymbolKind kind, double x, double y, string param, string expr, string unit)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = param, Expression = expr, Unit = unit });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EmLayoutSource Source(EmCellFixture fx)
    {
        var layout = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, fx.P.ClayPath, fx.Cws, new TechnologyCache());
        return new EmLayoutSource(fx.P.ClayPath, layout, tech.Tech, layout.DbuPerMicron);
    }

    private static Complex At(RfCore.Data.DataCube s, int f, int i, int j) => s.At("freq", f).At("i", i).At("j", j).ComplexValues[0];

    /// <summary>A passive five-port at 2 GHz: a stand-in result for two drawn ports and three cut pins.</summary>
    private static string FivePort()
    {
        var sb = new System.Text.StringBuilder("# GHz S RI R 50\n");
        foreach (int f in new[] { 2 })
        {
            for (int i = 0; i < 5; i++)
            {
                sb.Append(i == 0 ? f.ToString(System.Globalization.CultureInfo.InvariantCulture) : " ");
                for (int j = 0; j < 5; j++)
                {
                    double re = i == j ? 0.1 : (i, j) is (0, 3) or (3, 0) or (1, 2) or (2, 1) ? 0.9 : 0.02;
                    sb.Append(' ').Append(re.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(" 0");
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
