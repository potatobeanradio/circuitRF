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
