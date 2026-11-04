using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-10 §3 — EM components placed in a larger schematic. The parent <c>T1</c> places instances of C2 (the
/// branch-line coupler), each with a 50 Ω feed on its pin 4 from a Port; pin 4 is the far end of C2's L90 arm from its
/// chain's anchor, so a longer L90 moves it. Nothing here solves: each test reads a plan, a count, a probe or a file.
/// </summary>
public sealed class PlacedEmComponentTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl10-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private const string Cell = "C2-branch-line";

    private sealed record Parent(EmCellFixture Fx, string CellDir, string SchematicDir, string Csch, string LayoutDir, string Clay)
    {
        public SchematicEditModel Load()
        {
            var m = SchematicPersistence.LoadFromFile(Csch).model;
            m.SchematicDirectory = SchematicDir;
            return m;
        }

        public SchematicLayoutPlan Plan(SchematicEditModel model, LayoutView target) =>
            SchematicLayoutPlanner.Plan(model, target, SchematicDir, Fx.Workspace, LayoutDir, Fx.P.Tech, Fx.P.TechPath,
                                        DiskCellResolver.Instance, targetLayoutPath: Clay, connectParts: true);

        public LayoutView NewTarget() =>
            new() { TechRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(LayoutDir, Fx.P.TechPath)) };

        public SdlChecker.Prepared Prepared(SchematicEditModel model, LayoutView target) =>
            new(model, target, Fx.Workspace, CellDir, SchematicDir, Csch, LayoutDir, Clay, Fx.P.Tech, Fx.P.TechPath);
    }

    /// <summary>
    /// T1: a VAR block (w50p, Lf), and per instance a C2 placement, a feed line F_k (w50p by Lf) on its pin 4 and a Port
    /// P_k at the feed's far end. <paramref name="instances"/> gives each placement's own values; <paramref name="em"/> sets
    /// them to Model = EM, with an S-parameter analysis.
    /// </summary>
    private Parent T1(string tag, bool em, params (string Inst, (string Name, string Expr, string Unit)[] Own)[] instances)
    {
        var fx = EmCellFixture.Create(Cell, Path.Combine(_scratch, tag));
        // D8: the cell's symbol, pins numbered by its Ports' Num.
        string symDir = CellFolder.SubFolderPath(fx.P.CellDir, ViewType.Symbol);
        Directory.CreateDirectory(symDir);
        CircuitRF.Design.Symbol.SymbolPersistence.SaveToFile(Path.Combine(symDir, Cell + ".csym"),
                                                            AutoSymbolGenerator.Generate(Cell, 4));

        string cellDir = CellFolder.CreateCellFolder(fx.Workspace, "T1");
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(schDir);
        Directory.CreateDirectory(layoutDir);

        var c2 = SchematicPersistence.LoadFromFile(fx.Csch).model;
        var model = new SchematicEditModel { SchematicDirectory = schDir };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "VAR1", Symbol = SymbolKind.Var, X = -2000, Y = -2000,
            Parameters =
            {
                new EditableParameter { Name = "w50p", Expression = "280.152", Unit = "um" },
                new EditableParameter { Name = "Lf", Expression = "2", Unit = "mm" },
            },
        });
        int k = 0;
        foreach (var (inst, own) in instances)
        {
            k++;
            double x = k * 5000;
            var xk = new EditableComponent
            {
                InstanceName = inst, Symbol = SymbolKind.Generic, X = x, Y = 0,
                CellRef = CircuitRF.Design.Workspace.ExternalCellRef.MakeCellRef(schDir, fx.P.CellDir),
            };
            foreach (var (name, expr, unit) in own) xk.Parameters.Add(new EditableParameter { Name = name, Expression = expr, Unit = unit });
            if (em) xk.Parameters.Add(new EditableParameter { Name = ParametricModelRegistry.ModelParameter, Expression = ParametricModelRegistry.EmValue });
            var feed = c2.Components.Single(c => c.InstanceName == "TL1").Clone();
            feed.InstanceName = "F" + k.ToString(CultureInfo.InvariantCulture);
            (feed.X, feed.Y) = (x, -3000);
            Set(feed, "W", "w50p");
            Set(feed, "L", "Lf");
            var port = c2.Components.Single(c => c.InstanceName == "P1").Clone();
            port.InstanceName = "P" + k.ToString(CultureInfo.InvariantCulture);
            Set(port, "Num", k.ToString(CultureInfo.InvariantCulture));
            (port.X, port.Y) = (x - 1500, -4500);
            var ground = c2.Components.Single(c => c.InstanceName == "GND1").Clone();
            ground.InstanceName = "GND" + k.ToString(CultureInfo.InvariantCulture);
            (ground.X, ground.Y) = (x - 1500, -6000);
            model.Components.AddRange([xk, feed, port, ground]);
            model.SchematicDirectory = schDir;
            Label(model, port, 0, $"in{k}");
            Label(model, feed, 0, $"in{k}");
            Label(model, feed, 1, $"n{k}");
            Label(model, xk, 3, $"n{k}");
            Label(model, port, 1, $"g{k}");
            Label(model, ground, 0, $"g{k}");
        }
        if (em) model.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("8", "11", 4, SweepKind.Linear, "GHz", "GHz")]));
        string csch = Path.Combine(schDir, "T1.csch");
        SchematicPersistence.SaveToFile(csch, model, "T1");
        return new Parent(fx, cellDir, schDir, csch, layoutDir, Path.Combine(layoutDir, "T1.clay"));

        static void Set(EditableComponent c, string name, string expr)
        {
            var p = c.Parameters.Single(q => q.Name == name);
            (p.Expression, p.Unit) = (expr, "");
        }
    }

    /// <summary>A wire stub off <paramref name="comp"/>'s pin <paramref name="port"/> (0-based) carrying a net label.</summary>
    private static void Label(SchematicEditModel model, EditableComponent comp, int port, string net)
    {
        var def = model.PortDefsOf(comp).Single(d => d.PortIndex == port);
        var (x, y) = model.PortWorldOf(comp, def);
        double dx = Math.Sign(Math.Round(x - comp.X)), dy = dx == 0 ? (Math.Sign(Math.Round(y - comp.Y)) is var s && s != 0 ? s : -1) : 0;
        var wire = new EditableWire();
        wire.Points.Add((x, y));
        wire.Points.Add((x + dx * 100, y + dy * 100));
        model.Wires.Add(wire);
        var label = new EditableNetLabel { Name = net };
        label.AnchorToWire(wire, x + dx * 100, y + dy * 100);
        model.NetLabels.Add(label);
    }

    /// <summary>The port labels T1 places: P_k numbered k.</summary>
    private static Dictionary<string, string> Ports(int n) =>
        Enumerable.Range(1, n).ToDictionary(k => "P" + k.ToString(CultureInfo.InvariantCulture), k => k.ToString(CultureInfo.InvariantCulture));

    private static string CellDirOf(LayoutView layout, string id, string layoutDir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(CircuitRF.Design.Workspace.ExternalCellRef.ResolveCellDir(
            layout.Instances.Single(i => i.SchematicId == id).CellRef, layoutDir)!));

    private static IEnumerable<string> LvsErrors(string cellDir) =>
        LvsRun.Run(cellDir).Findings.Where(f => !f.Waived && f.Severity == DiagnosticSeverity.Error).Select(f => $"{f.Id}: {f.Render()}");

    /// <summary>
    /// D3: X1 at the cell's own values places the cell's own layout, X2 at a longer L90 a variant; X3 at X2's values
    /// reuses X2's variant; the cell's .clay is unchanged; every feed is joined to its coupler and LVS is clean.
    /// </summary>
    [Fact]
    public void Variants_PerInstance_SharedByGeometry_CellUntouched_AndJoined()
    {
        var t = T1("var", em: false, ("X1", []), ("X2", [("L90", "5.2", "mm")]), ("X3", [("L90", "5.2", "mm")]));
        string cellClay = File.ReadAllText(t.Fx.P.ClayPath);
        var model = t.Load();
        var plan = t.Plan(model, t.NewTarget());
        var layout = t.Prepared(model, t.NewTarget()).Applied(plan);

        Assert.Equal(Path.TrimEndingDirectorySeparator(t.Fx.P.CellDir), CellDirOf(layout, "X1", t.LayoutDir));
        string v2 = CellDirOf(layout, "X2", t.LayoutDir);
        Assert.True(GeneratedCellStore.IsVariant(v2), v2);
        Assert.Equal(v2, CellDirOf(layout, "X3", t.LayoutDir));
        Assert.Contains(plan.Lines, l => l.InstanceName == "X2" && l.Text.Contains("placed as a variant of C2-branch-line at its own values (L90 = 5.2 mm)"));
        Assert.Equal(cellClay, File.ReadAllText(t.Fx.P.ClayPath));

        var problems = SdlChecker.Verify(t.Prepared(model, t.NewTarget()), layout, joinable: ["n1", "n2", "n3"], ports: Ports(3));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>D4: with X2 held where it is (locked), lengthening its L90 carries the feed line's end with X2's pin 4;
    /// the copper stays joined and LVS stays clean.</summary>
    [Fact]
    public void Propagation_TheFeedFollowsTheVariantsPort()
    {
        var t = T1("prop", em: false, ("X1", []), ("X2", [("L90", "5.2", "mm")]));
        var model = t.Load();
        var layout = t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget()));
        layout.Instances.Single(i => i.SchematicId == "X2").Locked = true;
        LayoutPersistence.SaveToFile(t.Clay, layout);
        var before = layout.Instances.Single(i => i.SchematicId == "F2");
        (long bx, long by) = (before.X, before.Y);

        model.Components.Single(c => c.InstanceName == "X2").Parameters.Single(p => p.Name == "L90").Expression = "5.6";
        var plan = t.Plan(model, layout);
        var after = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(layout));
        plan.ApplyTo(after);
        var f2 = after.Instances.Single(i => i.SchematicId == "F2");
        Assert.NotEqual((bx, by), (f2.X, f2.Y));

        var problems = SdlChecker.Verify(t.Prepared(model, after), after, joinable: ["n1", "n2"], ports: Ports(2));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>D6: X1.w50 and X2.w50 are separate tunables; at different values they are two geometries, at equal values
    /// one — two instances at the same values cost one solve.</summary>
    [Fact]
    public void Independence_TwoInstances_SeparateTunables_OneSolveAtEqualValues()
    {
        var t = T1("ind", em: true, ("X1", [("w50", "290", "um")]), ("X2", [("w50", "300", "um")]));
        var catalog = TunableCatalog.Discover(t.Load(), DiskCellResolver.Instance, t.Fx.Workspace);
        Assert.NotNull(catalog.FindValue("X1.w50"));
        Assert.NotNull(catalog.FindValue("X2.w50"));

        EmSolveCountResult Count(params (string, string)[] tunables)
        {
            var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(t.Csch),
                new CircuitEvaluationRequest { Tunables = tunables.ToDictionary(x => x.Item1, x => x.Item2) });
            Assert.True(plan.Status == RunStatus.Success, plan.StatusMessage);
            return CircuitEvaluation.CountEmSolves(plan);
        }
        var apart = Count();
        Assert.Empty(apart.Refusals);
        Assert.Equal((2, 2), (apart.Evaluations, apart.Geometries));
        var same = Count(("X2.w50", "290 um"));
        Assert.Equal((2, 1), (same.Evaluations, same.Geometries));
    }

    /// <summary>D5: X2 and its feed both held (locked), so a longer L90 cannot be absorbed — X2 intact in itself, its pin 4
    /// pulled off the feed. The probe a candidate passes before any solve says so at the parent's level.</summary>
    [Fact]
    public void TwoLevelConnectivity_APortPulledOffItsFeed_IsADifferenceInTheParent()
    {
        var t = T1("two", em: true, ("X1", []), ("X2", [("L90", "4.6604", "mm")]));
        var model = t.Load();
        var layout = t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget()));
        foreach (var id in new[] { "X2", "F2" }) layout.Instances.Single(i => i.SchematicId == id).Locked = true;
        LayoutPersistence.SaveToFile(t.Clay, layout);

        var probe = new EmGeometryProbe(PreparedCircuit.FromSchematic(t.Csch), [], null);
        var start = probe.At(new Dictionary<string, string> { ["X2.L90"] = "4.6604 mm" });
        Assert.Null(start.Refusal);
        Assert.NotNull(start.Parent);
        var longer = probe.At(new Dictionary<string, string> { ["X2.L90"] = "5.2 mm" });
        Assert.NotNull(longer.Parent);
        Assert.NotNull(longer.Parent!.DifferenceFrom(start.Parent!));
        // X2's own copper is intact: the difference is the parent's alone.
        var own = (start.Records.Single(r => r.InstancePath == "X2").Connectivity!, longer.Records.Single(r => r.InstancePath == "X2").Connectivity!);
        Assert.Null(own.Item2.DifferenceFrom(own.Item1));
    }

    /// <summary>D7: Apply writes X1.w50 to X1 and a parent VAR to the parent's VAR block, and leaves the cell alone; Make
    /// These the Cell's Own Values lists both other placements and, confirmed, writes the cell and clears the override.</summary>
    [Fact]
    public void WriteBack_ByDefinitionSite_ThenMakeTheseTheCellsOwn()
    {
        var t = T1("wb", em: true, ("X1", []), ("X2", [("w50", "310", "um")]), ("X3", []));
        var model = t.Load();
        LayoutPersistence.SaveToFile(t.Clay, t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget())));
        string cellCsch = File.ReadAllText(t.Fx.Csch);

        var applied = EmApply.Apply(t.Csch, new Dictionary<string, string> { ["X1.w50"] = "300 um", ["w50p"] = "290 um" }, null);
        Assert.Null(applied.Refusal);
        Assert.Equal(["X1.w50", "w50p"], applied.Written.Order(StringComparer.Ordinal));
        var after = t.Load();
        var x1 = after.Components.Single(c => c.InstanceName == "X1").Parameters.Single(p => p.Name == "w50");
        Assert.Equal(("300", "um"), (x1.Expression, x1.Unit));
        var row = after.Components.Single(c => c.Symbol == SymbolKind.Var).Parameters.Single(p => p.Name == "w50p");
        Assert.Equal(("290", "um"), (row.Expression, row.Unit));
        Assert.Equal(cellCsch, File.ReadAllText(t.Fx.Csch));
        Assert.True(GeneratedCellStore.IsVariant(CellDirOf(LayoutPersistence.LoadFromFile(t.Clay), "X1", t.LayoutDir)));
        Assert.NotNull(applied.LayoutKey);

        var plan = CellOwnValues.Plan(t.Csch, "X1");
        Assert.Null(plan.Refusal);
        Assert.Equal(["X2", "X3"], plan.Others.Select(o => o.Instance).Order(StringComparer.Ordinal));
        Assert.Contains("w50: keeps its own 310 um", plan.Others.Single(o => o.Instance == "X2").Lines);
        Assert.Contains("w50: 280.152 um → 300 um", plan.Others.Single(o => o.Instance == "X3").Lines);
        Assert.Contains("X3 in T1.csch", plan.Summary());

        Assert.Equal(["w50"], CellOwnValues.Apply(plan));
        var cell = SchematicPersistence.LoadFromFile(t.Fx.Csch).model;
        var w50 = cell.Components.Single(c => c.Symbol == SymbolKind.Var).Parameters.Single(p => p.Name == "w50");
        Assert.Equal(("300", "um"), (w50.Expression, w50.Unit));
        Assert.Equal("", t.Load().Components.Single(c => c.InstanceName == "X1").Parameters.Single(p => p.Name == "w50").Expression);
    }

    /// <summary>D1/D2: a cell with only a 3D view whose dimension uses a VAR linked to its parameter is 3D-parametric with
    /// no circuit model: placed, it is EM-only; an optimisation with em= unstated selects direct and says why, and an
    /// explicit em=correct is refused naming the instance.</summary>
    [Fact]
    public void ThreeDParametric_NoCircuitModel_DirectByDefault_CorrectRefusedByName()
    {
        var fx = EmCellFixture.Create("C1-through-line", Path.Combine(_scratch, "3d"), edit: m => m.Components.Add(new EditableComponent
        {
            InstanceName = "VAR1", Symbol = SymbolKind.Var,
            Parameters = { new EditableParameter { Name = "w50", Expression = "1.1", Unit = "mm" } },
        }));
        File.Delete(fx.Cem);
        string dir = CellFolder.SubFolderPath(fx.P.CellDir, ViewType.ThreeD);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "C1-through-line" + C3dPersistence.Extension);
        var made = C3dHierarchy.NewFromLayout(fx.P.CellDir, path, fx.Cws);
        Assert.Null(made.Refusal);
        var doc = made.Document!;
        var e = C3dElaborator.ElaborateOnce(doc, path, fx.Cws);
        var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, path, fx.Cws, new TechnologyCache());
        var flat = C3dHierarchy.FlattenOne(doc, path, 0, e, tech.Tech, null, fx.Cws);
        Assert.True(flat.Refusal is null, flat.Refusal);
        doc.Instances.Clear();
        doc.Objects.AddRange(flat.Objects);
        var spacer = new C3dBox { Name = "Spacer", Material = "Laminate", Min = new C3dPoint3(0, 0, 2_000_000), Size = new C3dPoint3(1_000_000, 1_000_000, 0) };
        spacer.Exprs = new() { ["Size"] = [null, null, new C3dExpr("w50", "Mm")] };
        doc.Objects.Add(spacer);
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup
        {
            Name = "Driven1", Solver3D = Em3dSolverChoice.One(Em3dSolverId.Palace),
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        }));
        C3dPersistence.SaveToFile(path, doc);
        // No schematic and no layout: the 3D view is the cell, its w50 the cell's parameter.
        Directory.Delete(CellFolder.SubFolderPath(fx.P.CellDir, ViewType.Schematic), recursive: true);
        Directory.Delete(CellFolder.SubFolderPath(fx.P.CellDir, ViewType.Layout), recursive: true);
        string ccellPath = Path.Combine(fx.P.CellDir, CellFolder.CcellFileName);
        var ccell = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
        ccell.Parameters.Add(new CcellParameter { Name = "w50", DefaultExpression = "1.1", Unit = "mm" });
        ccell.PrimarySchematic = null;
        ccell.PrimaryLayout = null;
        CellPersistence.SaveToFile(ccellPath, ccell);

        var kind = EmComponentKinds.Of(fx.P.CellDir);
        Assert.Equal(EmComponentKind.ThreeDParametric, kind.Kind);
        Assert.False(kind.HasCircuitModel);
        Assert.Contains("simulated by EM only", kind.Sentence);
        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out var why);
        Assert.True(target is { ThreeDOnly: true }, why);
        var a = EmEvaluation.Geometry(target!, new Dictionary<string, Value> { ["w50"] = new(1.1e-3) }, out var r1);
        var b = EmEvaluation.Geometry(target!, new Dictionary<string, Value> { ["w50"] = new(1.5e-3) }, out var r2);
        Assert.True(a is not null && b is not null, r1 ?? r2);
        Assert.NotEqual(a!.Key, b!.Key);

        // Placed: ports, its parameter, no contents — an EM-only part.
        var lib = new Library("l");
        var cellDef = new Cell("C1-through-line");
        cellDef.Ports.AddRange(["p1", "p2"]);
        cellDef.Parameters.Add(new ParameterDeclaration("w50", "1.1", "mm"));
        lib.Cells.Add(cellDef);
        var tb = new TestBench("parent");
        for (int k = 1; k <= 2; k++)
            tb.Instances.Add(new Instance($"P{k}", "Port", [$"n{k}", "0"],
                [new ParameterAssignment("Num", k.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("Z", "50")]));
        tb.Instances.Add(new Instance("X1", "C1-through-line", ["n1", "n2"],
        [
            new(ParametricModelRegistry.ModelParameter, ParametricModelRegistry.EmValue),
            new(ParametricModelRegistry.CellParameter, "\"" + fx.P.CellDir + "\""),
            new("w50", "1.1", "mm"),
        ]));
        tb.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")]));
        Assert.Equal(["X1"], EmModelSwitch.WithoutCircuitModel(tb, lib));

        OptimizationRun Run(OptimizerEmMode? em)
        {
            tb.Tuning = new TuningSetup
            {
                Variables = [new TunableEntry { Key = "X1.w50", Opt = true, Min = "1 mm", Max = "1.5 mm", Scale = TuneScale.Lin }],
                Goals = [new OptimizationGoal { Name = "G", Expression = "dB(SP1.S(2,1))", Analysis = "SP1", Type = GoalType.Ge, Limit = "-1" }],
                Optimizer = new OptimizerSettings { Algorithm = "simplex", Em = em, Seed = 1 },
            };
            return OptimizationRun.Create(PreparedCircuit.FromBench(lib, tb, fx.Workspace));
        }
        var auto = Run(null);
        Assert.Null(auto.Refusal);
        Assert.Equal(OptimizerEmMode.Direct, auto.EmMode);
        var correct = Run(OptimizerEmMode.Correct);
        Assert.NotNull(correct.Refusal);
        Assert.Contains("X1", correct.Refusal!.Render());
        Assert.Contains("em=correct needs a circuit model", correct.Refusal.Render());

        var off = CircuitEvaluation.Plan(PreparedCircuit.FromBench(lib, tb, fx.Workspace), new CircuitEvaluationRequest { Em = false });
        Assert.Contains("X1 has no circuit model", off.StatusMessage);
    }

    /// <summary>D1: a one-off EM result placed in a schematic is a fixed component: explain --tunables names it with no
    /// variables of its own, and says why.</summary>
    [Fact]
    public void Fixed_AnEmResultCell_HasNoTunablesOfItsOwn_AndSaysWhy()
    {
        var t = T1("fixed", em: false, ("X1", []));
        string sim = CellFolder.CreateCellFolder(t.Fx.Workspace, "Line" + EmSnpSchematic.CellSuffix);
        string simSch = CellFolder.SubFolderPath(sim, ViewType.Schematic);
        Directory.CreateDirectory(simSch);
        var simModel = new SchematicEditModel();
        simModel.Components.Add(new EditableComponent
        {
            InstanceName = EmSnpSchematic.SnpInstanceName, Symbol = SymbolKind.Snp,
            Parameters = { new EditableParameter { Name = "File", Expression = "\"../results/Line.s2p\"" } },
        });
        SchematicPersistence.SaveToFile(Path.Combine(simSch, "Line_sim.csch"), simModel, "Line_sim");
        var kind = EmComponentKinds.Of(sim);
        Assert.Equal(EmComponentKind.Fixed, kind.Kind);
        Assert.False(kind.Varies);

        var model = t.Load();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X9", Symbol = SymbolKind.Generic, X = 30000, Y = 0,
            CellRef = CircuitRF.Design.Workspace.ExternalCellRef.MakeCellRef(t.SchematicDir, sim),
        });
        SchematicPersistence.SaveToFile(t.Csch, model, "T1");
        var (report, exit) = CircuitRF.Cli.ExplainTunables.Collect(t.Csch, CircuitRF.Cli.DocumentKind.Schematic);
        Assert.Equal(0, exit);
        Assert.Contains(report!.EmComponents!, l => l.StartsWith("X9: Fixed EM component", StringComparison.Ordinal)
                                                    && l.Contains("no variables of its own"));
        Assert.Contains(report.EmComponents!, l => l.StartsWith("X1: Schematic-driven", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Tunables, x => x.Key.StartsWith("X9.", StringComparison.Ordinal));
    }

    // ── Review fixes ─────────────────────────────────────────────────────────

    /// <summary>A variant and the PCells drawn inside it are live: the generated-cell prune a workspace open runs keeps
    /// them, and the live set an archive takes names them.</summary>
    [Fact]
    public void Variants_AreLive_SoThePruneOnOpenKeepsThem()
    {
        var t = T1("prune", em: false, ("X1", []), ("X2", [("L90", "5.2", "mm")]));
        var model = t.Load();
        LayoutPersistence.SaveToFile(t.Clay, t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget())));
        string v2 = CellDirOf(LayoutPersistence.LoadFromFile(t.Clay), "X2", t.LayoutDir);
        string variantClay = Path.Combine(CellFolder.SubFolderPath(v2, ViewType.Layout), Path.GetFileName(v2) + CellFolder.ViewExtension(ViewType.Layout));
        var inner = GeneratedCellsLifecycle.GeneratedCellRefs(LayoutPersistence.LoadFromFile(variantClay)).ToList();

        var live = GeneratedCellsLifecycle.LiveCells(t.Fx.Workspace);
        Assert.True(live.Complete);
        Assert.Contains(Path.GetFileName(v2), live.Cells.Keys);
        Assert.All(inner, n => Assert.Contains(n, live.Cells.Keys));

        GeneratedCellsLifecycle.RegenerateAll(t.Fx.Workspace, p => p is not null && File.Exists(p) ? TechPersistence.LoadFromFile(p) : null);
        Assert.True(File.Exists(variantClay));
        // Read again: a rebuild may have renamed a PCell, and the variant then names the new one.
        Assert.All(GeneratedCellsLifecycle.GeneratedCellRefs(LayoutPersistence.LoadFromFile(variantClay)),
                   n => Assert.True(Directory.Exists(Path.Combine(t.Fx.Workspace, GeneratedCellStore.ReservedFolderName, n)), n));
    }

    /// <summary>The workspace's session for the one cell schematic the test edits.</summary>
    private sealed class CellSessions(CircuitRF.Ui.ViewModels.SchematicViewModel session) : CircuitRF.Ui.Schematic.ICellSchematicSessions
    {
        public int Saved;

        public CircuitRF.Ui.ViewModels.SchematicViewModel CellSession(string cschPath) => session;

        public void SaveCellSession(string cschPath)
        {
            SchematicPersistence.SaveToFile(cschPath, session.EditModel, Path.GetFileNameWithoutExtension(cschPath));
            session.UndoRedo.MarkSaved();
            Saved++;
        }
    }

    /// <summary>In the GUI, Make These the Cell's Own Values writes a VAR through the session the workspace resolves the cell
    /// through, as an undo step there, and saves it; it refuses while that session has unsaved edits.</summary>
    [Fact]
    public void MakeTheseTheCellsOwn_InTheGui_WritesThroughTheCellsSession()
    {
        var t = T1("vm", em: false, ("X1", [("w50", "300", "um")]));
        var parentModel = t.Load();
        var parentVm = new CircuitRF.Ui.ViewModels.SchematicViewModel(parentModel);
        var cellModel = SchematicPersistence.LoadFromFile(t.Fx.Csch).model;
        cellModel.SchematicDirectory = Path.GetDirectoryName(t.Fx.Csch);
        var cellVm = new CircuitRF.Ui.ViewModels.SchematicViewModel(cellModel);
        var sessions = new CellSessions(cellVm);
        var editor = new CircuitRF.Ui.ViewModels.ParameterEditorViewModel { CellSessions = sessions };
        editor.SetTargetDirect(parentVm, parentModel.Components.Single(c => c.InstanceName == "X1"), showClose: false);

        var var1 = cellModel.Components.Single(c => c.Symbol == SymbolKind.Var);
        cellVm.Execute(new CircuitRF.Ui.Commands.Schematic.SetParametersCommand(cellModel, var1, var1.Parameters));
        Assert.Contains("not saved", editor.PlanCellOwnValues()!.Refusal);
        cellVm.UndoRedo.Undo();

        var plan = editor.PlanCellOwnValues()!;
        Assert.Null(plan.Refusal);
        string status = editor.ApplyCellOwnValues(plan);
        var w50 = var1.Parameters.Single(p => p.Name == "w50");
        Assert.Equal(("300", "um"), (w50.Expression, w50.Unit));
        Assert.Equal(1, sessions.Saved);
        Assert.Equal("300", SchematicPersistence.LoadFromFile(t.Fx.Csch).model.Components.Single(c => c.Symbol == SymbolKind.Var)
                                                .Parameters.Single(p => p.Name == "w50").Expression);
        Assert.Equal("", parentModel.Components.Single(c => c.InstanceName == "X1").Parameters.Single(p => p.Name == "w50").Expression);
        Assert.Contains($"Undo here restores X1's values only; the VAR change is undone in {Cell}'s schematic", status);
        cellVm.UndoRedo.Undo();
        Assert.Equal("280.152", var1.Parameters.Single(p => p.Name == "w50").Expression);
    }

    /// <summary>Apply in a parent that has no layout made from it writes the values and succeeds, saying there was no layout
    /// geometry to check, instead of failing a key comparison that has no layout.</summary>
    [Fact]
    public void Apply_InAParentWithNoLayout_SucceedsWithNothingToVerify()
    {
        var t = T1("nolayout", em: true, ("X1", []));
        var applied = EmApply.Apply(t.Csch, new Dictionary<string, string> { ["X1.w50"] = "300 um" }, "0123456789abcdef");
        Assert.Null(applied.Refusal);
        Assert.Equal(["X1.w50"], applied.Written);
        Assert.NotNull(applied.NothingToVerify);
        Assert.True(applied.Succeeded, applied.Sentence);
        Assert.Contains("no layout made from it", applied.Sentence);
    }

    /// <summary>The parent's connectivity probe holds the variants a candidate needs in memory: it writes no variant folder.</summary>
    [Fact]
    public void ParentProbe_WritesNoVariantFolder()
    {
        var t = T1("probe", em: true, ("X1", []), ("X2", [("L90", "4.6604", "mm")]));
        var model = t.Load();
        LayoutPersistence.SaveToFile(t.Clay, t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget())));
        string gen = Path.Combine(t.Fx.Workspace, GeneratedCellStore.ReservedFolderName);
        string[] Variants() => Directory.Exists(gen)
            ? [.. Directory.GetDirectories(gen).Where(GeneratedCellStore.IsVariant).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal)]
            : [];
        var before = Variants();

        var probe = new EmGeometryProbe(PreparedCircuit.FromSchematic(t.Csch), [], null);
        var at = probe.At(new Dictionary<string, string> { ["X2.L90"] = "5.4 mm" });
        Assert.Null(at.Refusal);
        Assert.NotNull(at.Parent);
        Assert.Null(at.ParentUnchecked);
        Assert.Equal(before, Variants());
    }

    /// <summary>An EM component placed inside another placed cell is named as not checked: the parent's layout shows that
    /// cell's own layout.</summary>
    [Fact]
    public void NestedEmComponents_AreNamed()
    {
        var t = T1("nest", em: false, ("X1", []));
        string y = CellFolder.CreateCellFolder(t.Fx.Workspace, "Y");
        string ySch = CellFolder.SubFolderPath(y, ViewType.Schematic);
        Directory.CreateDirectory(ySch);
        var ym = new SchematicEditModel { SchematicDirectory = ySch };
        ym.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = ExternalCellRef.MakeCellRef(ySch, t.Fx.P.CellDir),
        });
        SchematicPersistence.SaveToFile(Path.Combine(ySch, "Y.csch"), ym, "Y");
        var layout = new LayoutView();
        layout.Instances.Add(new LayoutInstance { SchematicId = "Y1", CellRef = ExternalCellRef.MakeCellRef(t.LayoutDir, y) });
        Assert.Equal(["Y1.X1"], EmApply.NestedEmComponents(layout, t.LayoutDir));
    }

    /// <summary>A layout's port-label pins follow the label when the view is edited in place: they are not cached on it.</summary>
    [Fact]
    public void PortLabelPins_FollowAnInPlaceEdit()
    {
        var view = new LayoutView();
        var label = new LabelShape { X = 1000, Y = 0, Text = "1", Height = 500, IsPort = true, SchematicId = "P1", PortDirection = LayoutRotation.R0 };
        view.Shapes.Add(label);
        Assert.Equal(1000, CellPins.Resolve(view, null).Single().X);
        label.X = 4000;
        Assert.Equal(4000, CellPins.Resolve(view, null).Single().X);
    }

    /// <summary>A variant found already on disk takes the cell's current symbol: what is not in its key is refreshed.</summary>
    [Fact]
    public void Variant_OnAHit_TakesTheCellsCurrentSymbol()
    {
        var t = T1("fresh", em: false, ("X2", [("L90", "5.2", "mm")]));
        var model = t.Load();
        var layout = t.Prepared(model, t.NewTarget()).Applied(t.Plan(model, t.NewTarget()));
        string v2 = CellDirOf(layout, "X2", t.LayoutDir);
        string cellSym = Path.Combine(CellFolder.SubFolderPath(t.Fx.P.CellDir, ViewType.Symbol), Cell + ".csym");
        string variantSym = Path.Combine(CellFolder.SubFolderPath(v2, ViewType.Symbol), Cell + ".csym");
        File.AppendAllText(cellSym, "\n");
        Assert.NotEqual(File.ReadAllBytes(cellSym), File.ReadAllBytes(variantSym));

        t.Plan(model, layout);
        Assert.Equal(File.ReadAllBytes(cellSym), File.ReadAllBytes(variantSym));
    }

    /// <summary>Make These the Cell's Own Values lists placements in the parent's workspace as well as the cell's, when the
    /// cell is taken from another workspace.</summary>
    [Fact]
    public void MakeTheseTheCellsOwn_ListsPlacementsInTheParentsWorkspaceToo()
    {
        var t = T1("ws", em: false, ("X1", []));
        var consumer = EmCellFixture.Create("C1-through-line", Path.Combine(_scratch, "ws-consumer"));
        string Place(string name, string inst, string? w50)
        {
            string dir = CellFolder.CreateCellFolder(consumer.Workspace, name);
            string sch = CellFolder.SubFolderPath(dir, ViewType.Schematic);
            Directory.CreateDirectory(sch);
            var m = new SchematicEditModel { SchematicDirectory = sch };
            var c = new EditableComponent { InstanceName = inst, Symbol = SymbolKind.Generic, CellRef = ExternalCellRef.MakeCellRef(sch, t.Fx.P.CellDir) };
            if (w50 is not null) c.Parameters.Add(new EditableParameter { Name = "w50", Expression = w50, Unit = "um" });
            m.Components.Add(c);
            string path = Path.Combine(sch, name + ".csch");
            SchematicPersistence.SaveToFile(path, m, name);
            return path;
        }
        string parent = Place("P", "X7", "300");
        Place("Q", "X8", null);

        var plan = CellOwnValues.Plan(parent, "X7");
        Assert.Null(plan.Refusal);
        Assert.Equal(["X1", "X8"], plan.Others.Select(o => o.Instance).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// check and explain on a parent placing EM components: the instance's EmCell= is relative to the run's base, so a
    /// read-only verb that elaborated without it reported an error the run does not have (or, run from the workspace
    /// folder, found the cell and solved it). Both resolve it now and solve nothing.
    /// </summary>
    [Fact]
    public void CheckAndExplain_OnAParentPlacingEmComponents_ResolveTheCell_AndSolveNothing()
    {
        var t = T1("check", em: true, ("X1", []), ("X2", [("L90", "5.2", "mm")]));
        int solves = 0;
        EmEvaluation.SolverStandIn = (_, _, _) => { Interlocked.Increment(ref solves); return null; };
        try
        {
            Assert.Equal(0, CircuitRF.Cli.Check.Run([t.Csch]));
            CircuitRF.Cli.Explain.Run([t.Csch, "--analysis"]);
            CircuitRF.Cli.Explain.Run([t.Csch, "--expr", "w50p*2"]);
            Assert.Equal(0, solves);
        }
        finally { EmEvaluation.SolverStandIn = null; }
    }
}
