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
/// two resistors and a capacitor whose value is the schematic's VAR <c>cm</c>, laid out as F7's three-pad package. A sub-cell with
/// a layout is copper to <c>"*"</c> (ICP-0 D3), so the EM view names Q1; the solve cuts it out at its cell edge with an Uncalibrated
/// Port where each conductor crosses it (ICP-2), and the bench puts Dev back on them from the schematic being run.
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
