using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-6 §5 — everything about simulating a schematic by EM of its own layout that does not need a solve: the
/// count and the cache over a sweep, the refusals, D4's unreached variable, Path B against Path A, and D7's interface
/// and tunable keys. Results stand in the cache as written files, so a run that would solve is visible as a new file.
/// </summary>
public sealed class ParametricEmStubTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl6s-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Path A, sweep: a parametric sweep over C2's L90 run by EM evaluates every point and resolves each distinct
    /// geometry once — two values that round to the same database unit share one result — and a run whose geometries
    /// are in the cache solves nothing, leaving the .csch, .clay and .cem as they were.
    /// </summary>
    [Fact]
    public void PathA_Sweep_EachDistinctGeometryOnce_FromTheCache_AndNoUserFileWritten()
    {
        double l90 = 4.6604e-3;
        var sweep = new ParametricSweepAnalysis("SW1", "L90", [l90, l90 + 1e-13, 5e-3], "SP1");
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, AnalysisModel.Em, sweep);
        var before = fx.UserFileBytes();

        var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest());
        var count = CircuitEvaluation.CountEmSolves(plan);
        Assert.Empty(count.Refusals);
        Assert.Equal((3, 2, 0, 2), (count.Evaluations, count.Geometries, count.Cached, count.ToSolve));
        Assert.Contains("2 EM solves needed for 3 evaluations", count.Sentence);
        Assert.Contains("more than the limit of 1", CircuitEvaluation.EmLimitRefusal(count, 1));
        Assert.Null(CircuitEvaluation.EmLimitRefusal(count, 2));

        // Both geometries "solved" as files in the cache: the run reads them and solves nothing.
        string dir = EmCellCache.CellFolder(fx.Workspace, "C2-branch-line");
        Directory.CreateDirectory(dir);
        foreach (var key in count.Records.Select(r => r.Key!).Distinct())
            File.WriteAllText(Path.Combine(dir, key + ".s4p"), FourPort(0.1 * (1 + Array.IndexOf(count.Records.Select(r => r.Key).Distinct().ToArray(), key))));
        int files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;

        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest { EmMaxSolves = 0 });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var s = run.Results.Single(r => r.Name == "SP1").Data["S"];
        Assert.Equal("L90", s.Axes[0].Name);
        Assert.Equal(3, s.Axes[0].Length);
        Assert.Equal(files, Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(before, fx.UserFileBytes());
    }

    /// <summary>Path A, mixed: F1's capacitor is a part, not copper — and a planar setup makes no component ports, so it is
    /// refused by name (src/Design/RESOLVED.md); an ideal R with no layout is refused by name.</summary>
    [Fact]
    public void PathA_APartAndAnIdealR_AreRefusedByName()
    {
        var f1 = EmCellFixture.Create("F1-dc-block", _scratch, AnalysisModel.Em);
        var r1 = CircuitEvaluation.CountEmSolves(CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(f1.Csch), new CircuitEvaluationRequest()));
        var refusal = Assert.Single(r1.Refusals);
        Assert.Contains("C1 (", refusal);
        Assert.Contains("not copper", refusal);

        var h1b = EmCellFixture.Create("H1b-through-ideal-r", _scratch, AnalysisModel.Em);
        var r2 = CircuitEvaluation.CountEmSolves(CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(h1b.Csch), new CircuitEvaluationRequest()));
        Assert.Contains("R1 (", Assert.Single(r2.Refusals));
        Assert.Contains("has no layout", r2.Refusals[0]);
    }

    /// <summary>EM capability is said, never assumed: a layout with no setup, and one with two, are refusals naming what
    /// is missing; naming one of the two resolves it.</summary>
    [Fact]
    public void Capability_NoSetupAndSeveralSetups_AreRefusals()
    {
        var fx = EmCellFixture.Create("C1-through-line", _scratch);
        File.Delete(fx.Cem);
        Assert.Null(EmCapability.Resolve(fx.P.CellDir, null, null, out var none));
        Assert.Contains("No EM setup analyses", none);

        EmSetupCreate.Create(fx.Workspace, "A", EmSetupCreate.LayoutRefOf(fx.Workspace, fx.P.ClayPath));
        EmSetupCreate.Create(fx.Workspace, "B", EmSetupCreate.LayoutRefOf(fx.Workspace, fx.P.ClayPath));
        Assert.Null(EmCapability.Resolve(fx.P.CellDir, null, null, out var several));
        Assert.Contains("has 2 EM setups", several);
        Assert.Contains("emSetup=", several);
        Assert.Equal("B", Path.GetFileNameWithoutExtension(EmCapability.Resolve(fx.P.CellDir, "B", null, out _)!.CemPath));
    }

    /// <summary>D4: ML3's W set in the layout — kept over the schematic's on every re-plan — means w50 no longer reaches it,
    /// and the evaluation says so.</summary>
    [Fact]
    public void D4_AVariableThatDoesNotReachTheCopper_IsSaid()
    {
        var fx = EmCellFixture.Create("C2-branch-line", _scratch);
        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;
        Assert.Empty(EmEvaluation.Geometry(target, new Dictionary<string, CircuitRF.Core.Expressions.Value>(), out _)!.Unreached);

        var layout = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        var ml3 = layout.Instances.Single(i => i.SchematicId == "ML3");
        var origin = CellLayoutResolver.Resolve(ml3.CellRef, fx.P.LayoutDir).View!.PCellOrigin!;
        string cell = GeneratedCellStore.GetOrCreate(fx.Workspace, "MLIN",
            new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 300e-6 }, fx.P.Tech, fx.P.TechPath, PCellLayerSelection.Default);
        ml3.CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(fx.P.LayoutDir, cell));
        LayoutPersistence.SaveToFile(fx.P.ClayPath, layout);

        var g = EmEvaluation.Geometry(target, new Dictionary<string, CircuitRF.Core.Expressions.Value>(), out _)!;
        Assert.Contains("w50 does not reach ML3.W, which is set in the layout.", g.Unreached);
    }

    /// <summary>
    /// Path B identity: C2 placed as one part with Model = EM, its Ports its pins (D8), is the same geometry as C2 run by
    /// EM of its own layout at the same values — so the same cached result; and an instance's w50 reaches the copper
    /// exactly as a --set of the schematic's own w50 does.
    /// </summary>
    [Fact]
    public void PathB_ThePlacedCell_IsPathAAtTheSameValues()
    {
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, AnalysisModel.Em);
        Assert.Equal(4, CellPortCount.Resolve(fx.P.CellDir));

        string KeyOfPathA(params (string, string)[] sets)
        {
            var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest { Sets = sets });
            Assert.True(plan.Status == RunStatus.Success, plan.StatusMessage);
            return CircuitEvaluation.CountEmSolves(plan).Records.Single().Key!;
        }

        string KeyOfPathB(string? w50)
        {
            var (lib, cellName, _) = CircuitRF.Design.Schematic.NetExtractor.ExtractCell(fx.P.CellDir, out var why)!.Value;
            Assert.Null(why);
            Assert.Equal(4, lib.Find(cellName)!.Ports.Count);
            var tb = new TestBench("parent");
            for (int k = 1; k <= 4; k++)
                tb.Instances.Add(new Instance($"P{k}", "Port", [$"n{k}", "0"],
                    [new ParameterAssignment("Num", k.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("Z", "50")]));
            var overrides = new List<ParameterAssignment>
            {
                new(ParametricModelRegistry.ModelParameter, ParametricModelRegistry.EmValue),
                new(ParametricModelRegistry.CellParameter, "\"" + fx.P.CellDir + "\""),
            };
            if (w50 is not null) overrides.Add(new ParameterAssignment("w50", w50, "um"));
            tb.Instances.Add(new Instance("X1", cellName, ["n1", "n2", "n3", "n4"], overrides));
            tb.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")]));
            var plan = CircuitEvaluation.Plan(PreparedCircuit.FromBench(lib, tb, fx.Workspace), new CircuitEvaluationRequest());
            Assert.True(plan.Status == RunStatus.Success, plan.StatusMessage);
            var record = CircuitEvaluation.CountEmSolves(plan).Records.Single();
            Assert.Null(record.Refusal);
            return record.Key!;
        }

        Assert.Equal(KeyOfPathA(), KeyOfPathB(null));
        Assert.Equal(KeyOfPathA(("w50", "300e-6")), KeyOfPathB("300"));
        Assert.NotEqual(KeyOfPathA(), KeyOfPathB("300"));
    }

    /// <summary>D7: an overridden cell VAR is a tunable keyed as a parameter is (X1.w50, not X1:w50, which is a cell's own
    /// variable), and a cell's VAR names are part of its interface — a rename changes the hash, a cell with none keeps
    /// the hash it always had.</summary>
    [Fact]
    public void D7_TheOverriddenVariableIsATunable_AndVariableNamesAreInterface()
    {
        var cell = new Cell("Line");
        cell.Ports.AddRange(["a", "b"]);
        cell.Variables.Add(new Variable("w50", "1"));
        cell.Instances.Add(new Instance("R1", "R", ["a", "b"], [new ParameterAssignment("R", "w50")]));
        var lib = new Library("l");
        lib.Cells.Add(cell);
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "Line", ["in", "0"], [new ParameterAssignment("w50", "2")]));
        tb.Instances.Add(new Instance("X2", "Line", ["in", "0"]));
        var catalog = TunableCatalog.FromNetlist(tb, lib);
        Assert.NotNull(catalog.FindValue("X1.w50"));
        Assert.Null(catalog.FindValue("X2.w50"));

        var symbol = new CircuitRF.Design.Symbol.Symbol([], [new CircuitRF.Design.Symbol.SymbolPin(-200, 0, 1, "a")], 1);
        string plain = CellInterfaceHash.Of(symbol, null);
        Assert.Equal(plain, CellInterfaceHash.Of(symbol, null, []));
        Assert.NotEqual(plain, CellInterfaceHash.Of(symbol, null, ["w50"]));
        Assert.NotEqual(CellInterfaceHash.Of(symbol, null, ["w50"]), CellInterfaceHash.Of(symbol, null, ["w51"]));
    }

    /// <summary>R-sdl6-3: a trace of an analysis that ran by EM says "(EM)" after its name; one of the same analysis run by
    /// circuit models does not. Read off the analysis group's <c>__EmModel</c> record, which the result keeps.</summary>
    [Fact]
    public void ATraceOfAnEmRun_IsLabelledEm()
    {
        DataSet Run(bool em)
        {
            var ds = new DataSet();
            ds.AddToGroup("SP1", "Gain", new DataCube([new Axis("freq", [1e9, 2e9], "Hz")], [1.0, 2.0]));
            if (em) ds.AddToGroup("SP1", EmResultStamp.CubeName,
                                  new DataCube([new Axis(EmResultStamp.AxisName, [0.0], "", ["model = em"])], [0.0]));
            return ds;
        }
        Trace TraceOf(DataSet ds)
        {
            var t = new Trace(new RfCore.SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            { CubeName = "SP1.Gain", Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)] };
            TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
            return t;
        }

        Assert.EndsWith("(EM)", TraceOf(Run(em: true)).ShortDescription);
        Assert.DoesNotContain("(EM)", TraceOf(Run(em: false)).ShortDescription);
    }

    // ── Review fixes ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A blank row on a cell instance is no override, whatever it names: the Parameter dialog's blank EmSetup row
    /// and a VAR row left behind by a rename are dropped, so neither reaches the netlist as <c>Key=</c> — which the reader
    /// would complete with the NEXT parameter, losing that one.</summary>
    [Fact]
    public void Extraction_ABlankInstanceRow_IsNoOverride_AndTakesNoNeighbour()
    {
        var fx = EmCellFixture.Create("C2-branch-line", _scratch);
        string symDir = CellFolder.SubFolderPath(fx.P.CellDir, ViewType.Symbol);
        Directory.CreateDirectory(symDir);
        SymbolPersistence.SaveToFile(Path.Combine(symDir, "C2-branch-line.csym"), AutoSymbolGenerator.Generate("C2-branch-line", 4));
        string parentDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(fx.Workspace, "Parent"), ViewType.Schematic);
        var parent = new SchematicEditModel { SchematicDirectory = parentDir };
        parent.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = ExternalCellRef.MakeCellRef(parentDir, fx.P.CellDir),
            Parameters =
            {
                new EditableParameter { Name = ParametricModelRegistry.ModelParameter, Expression = ParametricModelRegistry.CircuitValue },
                new EditableParameter { Name = ParametricModelRegistry.SetupParameter, Expression = "" },
                new EditableParameter { Name = "w50old", Expression = "" },
                new EditableParameter { Name = "w50", Expression = "300", Unit = "um" },
            },
        });

        var extracted = NetExtractor.Extract(parent, "tb", DiskCellResolver.Instance);
        Assert.DoesNotContain(extracted.TestBench.Instances.Single(i => i.InstanceName == "X1").Overrides,
                              o => string.IsNullOrWhiteSpace(o.Expression));
        var (_, tb) = SchematicCircuit.RoundTrip(SchematicCircuit.CnlTextOf(extracted, "tb"), "tb", fx.Workspace);
        Assert.Equal("300", tb.Instances.Single(i => i.InstanceName == "X1").Overrides.Single(o => o.Name == "w50").Expression);
    }

    /// <summary>C1 placing X1, an instance of a cell with a schematic and no layout view. The planner skips it, so it is
    /// not in the copper.</summary>
    private EmCellFixture WithSchematicOnlyCell(string tag) => EmCellFixture.Create("C1-through-line", Path.Combine(_scratch, tag),
        AnalysisModel.Em, edit: model =>
        {
            string ws = Path.GetFullPath(Path.Combine(_scratch, tag, "C1-through-line", "ws"));
            string rc = CellFolder.CreateCellFolder(ws, "RC");
            var cell = new SchematicEditModel();
            foreach (int k in new[] { 1, 2 })
                cell.Components.Add(new EditableComponent
                {
                    InstanceName = "Pin" + k, Symbol = SymbolKind.Pin, X = k * 400,
                    Parameters = { new EditableParameter { Name = "Num", Expression = k.ToString(CultureInfo.InvariantCulture) } },
                });
            cell.Components.Add(new EditableComponent
            {
                InstanceName = "R1", Symbol = SymbolKind.Resistor, Y = 600,
                Parameters = { new EditableParameter { Name = "R", Expression = "50", Unit = "Ohm" } },
            });
            SchematicPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(rc, ViewType.Schematic), "RC.csch"), cell, "RC");
            SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(rc, ViewType.Symbol), "RC.csym"), AutoSymbolGenerator.Generate("RC", 2));
            string schDir = CellFolder.SubFolderPath(Path.Combine(ws, "C1-through-line"), ViewType.Schematic);
            model.Components.Add(new EditableComponent
            {
                InstanceName = "X1", Symbol = SymbolKind.Generic, X = 0, Y = 5000, CellRef = ExternalCellRef.MakeCellRef(schDir, rc),
            });
        });

    /// <summary>R-sdl6-2: a sub-cell with no layout view is not copper — it is refused by name, never left out.</summary>
    [Fact]
    public void PathA_ASubCellWithNoLayoutView_IsRefusedByName()
    {
        var fx = WithSchematicOnlyCell("nolayout-a");
        var count = CircuitEvaluation.CountEmSolves(CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest()));
        var refusal = Assert.Single(count.Refusals);
        Assert.Contains("X1 (", refusal);
        Assert.Contains("has no layout", refusal);
    }

    /// <summary>R-sdl6-2: an evaluation whose re-plan skips a part is refused naming it — the copper would not be the
    /// circuit drawn (the path a placed cell takes, which has no Path A classification ahead of it).</summary>
    [Fact]
    public void Evaluation_APartThePlannerSkips_IsARefusal()
    {
        var fx = WithSchematicOnlyCell("nolayout-b");
        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;
        Assert.Null(EmEvaluation.Geometry(target, new Dictionary<string, CircuitRF.Core.Expressions.Value>(), out var refusal));
        Assert.Contains("X1 (", refusal);
        Assert.Contains("leave them out", refusal);
    }

    /// <summary>D5: a sweep whose points do not include the schematic's own values solves exactly what the count said —
    /// its points — and not the saved geometry as well.</summary>
    [Fact]
    public void D5_ASweepSolvesItsPointsOnly_NotTheSchematicsOwnValues()
    {
        var sweep = new ParametricSweepAnalysis("SW1", "L90", [4.0e-3, 5.0e-3], "SP1");
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, AnalysisModel.Em, sweep);
        var count = CircuitEvaluation.CountEmSolves(CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest()));
        Assert.Equal(2, count.ToSolve);

        int solves = 0;
        EmEvaluation.SolverStandIn = (_, _, path) =>
        {
            Interlocked.Increment(ref solves);
            File.WriteAllText(path + ".s4p", FourPort(0.1));
            return path + ".s4p";
        };
        try
        {
            var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest { EmMaxSolves = 2 });
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        }
        finally { EmEvaluation.SolverStandIn = null; }
        Assert.Equal(count.ToSolve, solves);
    }

    /// <summary>The CLI switches the chain its verb dispatches: a sweep named with <c>--analysis</c>, in another case, is
    /// switched to its EM bench — before, only a base analysis of that exact spelling was looked for, and the run went
    /// ahead by circuit models.</summary>
    [Fact]
    public void Cli_ANamedSweep_InAnotherCase_IsSwitchedToEm()
    {
        var sweep = new ParametricSweepAnalysis("SW1", "L90", [4.6604e-3], "SP1");
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, AnalysisModel.Em, sweep);
        var circuit = PreparedCircuit.FromSchematic(fx.Csch);
        var (lib, tb) = (circuit.Lib!, circuit.Tb!);
        using var json = CircuitRF.Cli.JsonRun.Isolate();
        using var scope = new CircuitRF.Cli.EmModelRun().Prepare(fx.Csch, SchematicCircuit.ReferenceBaseOf(fx.Csch), ref lib, ref tb,
                                                                 a => a is SParameterAnalysis, "sw1", out int exit);
        Assert.Equal(0, exit);
        Assert.Contains(tb.Instances, i => i.Reference == EmModelSwitch.PathAReference);
    }

    /// <summary>D4 follows the VAR rows as drawn: with w50 = w0 and ML3.W held in the layout, an evaluation that moves w0
    /// (and so w50) names w0 too — the variable actually varied — not only the w50 its value was applied to.</summary>
    [Fact]
    public void D4_TheRootVariable_IsNamed_AtValuesThatMoveIt()
    {
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, edit: m =>
        {
            var var1 = m.Components.Single(c => c.InstanceName == "VAR1");
            var w50 = var1.Parameters.Single(p => p.Name == "w50");
            var1.Parameters.Add(new EditableParameter { Name = "w0", Expression = w50.Expression, Unit = w50.Unit });
            w50.Expression = "w0";   // keeps its unit, so the use sites that state one still read it as a length
        });
        var layout = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        var ml3 = layout.Instances.Single(i => i.SchematicId == "ML3");
        var origin = CellLayoutResolver.Resolve(ml3.CellRef, fx.P.LayoutDir).View!.PCellOrigin!;
        string cell = GeneratedCellStore.GetOrCreate(fx.Workspace, "MLIN",
            new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 300e-6 }, fx.P.Tech, fx.P.TechPath, PCellLayerSelection.Default);
        ml3.CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(fx.P.LayoutDir, cell));
        LayoutPersistence.SaveToFile(fx.P.ClayPath, layout);

        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;
        var values = new Dictionary<string, CircuitRF.Core.Expressions.Value>
        {
            ["w0"] = new(320e-6), ["w50"] = new(320e-6),
        };
        var g = EmEvaluation.Geometry(target, values, out var why);
        Assert.True(g is not null, why);
        Assert.Contains("w0 does not reach ML3.W, which is set in the layout.", g!.Unreached);
    }

    /// <summary>D3: artwork on a patterned dielectric's MASK layer decides whether the film is in the medium, so it is in
    /// the key; with no tie to it, the same artwork is not.</summary>
    [Fact]
    public void Cache_ArtworkOnAMaskLayer_ChangesTheKey()
    {
        var fx = EmCellFixture.Create("C1-through-line", _scratch);
        var setup = EmSetupPersistence.LoadFromFile(fx.Cem);
        var layout = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        var masked = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        masked.Shapes.Add(new RectShape { Layer = new LayerKey(3, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        string KeyOf(LayoutView view, Technology tech) =>
            EmCellCache.KeyOf(new EmLayoutSource(fx.P.ClayPath, view, tech, view.DbuPerMicron), setup);

        var plain = TechPersistence.LoadFromFile(fx.P.TechPath);
        Assert.Equal(KeyOf(layout, plain), KeyOf(masked, plain));

        var tied = TechPersistence.LoadFromFile(fx.P.TechPath);
        tied.Stackup.Layers.Single(l => l.Kind == StackupKind.Dielectric).PresentWithLayer =
            tied.Layers.Single(l => l.Key == new LayerKey(3, 0)).Name;
        Assert.NotEqual(KeyOf(layout, tied), KeyOf(masked, tied));
    }

    /// <summary>D3: the cache reads only a finished result — a Touchstone left under its write-time name by a solve that
    /// stopped mid-write is never returned as solved.</summary>
    [Fact]
    public void Cache_APartialWrite_IsNeverRead()
    {
        string dir = EmCellCache.CellFolder(_scratch, "Cell");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "0123456789abcdef.partial-1a2b3c4d.s4p"), "# GHz S RI R 50\n1");
        Assert.Null(EmCellCache.Find(_scratch, "Cell", "0123456789abcdef"));
    }

    /// <summary>An EM cache folder that cannot be made is a refusal sentence, not an exception out of the elaboration.</summary>
    [Fact]
    public void Cache_AFolderThatCannotBeMade_IsARefusal()
    {
        var fx = EmCellFixture.Create("C1-through-line", _scratch);
        string root = EmCellCache.Root(fx.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        File.WriteAllText(root, "a file where the cache folder goes");
        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;

        var result = EmEvaluation.Evaluate(target, new Dictionary<string, CircuitRF.Core.Expressions.Value>());
        Assert.Null(result.SnpPath);
        Assert.Contains("nowhere to be kept", result.Refusal);
    }

    /// <summary>Reordering a sweep keeps what it is simulated by.</summary>
    [Fact]
    public void ReorderingASweep_KeepsItsModel()
    {
        var model = new SchematicEditModel();
        model.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz")]));
        var inner = new ParametricSweepAnalysis("SW1", "a", [1.0, 2.0], "SP1") { Model = AnalysisModel.Em, EmSetup = "Setup" };
        model.Analyses.Add(inner);
        model.Analyses.Add(new ParametricSweepAnalysis("SW2", "b", [1.0, 2.0], "SW1"));
        new CircuitRF.Ui.Commands.Analysis.ReorderSweepInChainCommand(model, inner, moveInner: false).Execute();
        var moved = model.Analyses.OfType<ParametricSweepAnalysis>().Single(s => s.Name == "SW1");
        Assert.Equal((AnalysisModel.Em, "Setup"), (moved.Model, moved.EmSetup));
    }

    /// <summary>A four-port Touchstone over the fixture's 1-3 GHz, |S21| set by <paramref name="s21"/> — a stand-in for an
    /// EM result in the cache.</summary>
    private static string FourPort(double s21)
    {
        var sb = new StringBuilder("# GHz S RI R 50\n");
        foreach (int f in new[] { 1, 2, 3 })
        {
            for (int i = 0; i < 4; i++)
            {
                sb.Append(i == 0 ? f.ToString(CultureInfo.InvariantCulture) : " ");
                for (int j = 0; j < 4; j++)
                    sb.Append(' ').Append((i != j ? s21 : 0.0).ToString("R", CultureInfo.InvariantCulture)).Append(" 0");
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
