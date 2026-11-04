using System.Globalization;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-7 §3 — optimising copper geometry by EM, in the designer's own schematic. Every solve here is the STUB EM
/// provider: the same schematic's circuit model with a known error (every length 3 % longer, every width 10 µm wider),
/// written as the Touchstone a real solve would write, so where EM's optimum lies is known and nothing meshes. C2 is the
/// branch-line coupler; its analysis is moved to 8-11 GHz, where its quarter-wave arms are, so a length moves the response.
/// </summary>
public sealed class EmOptimizationTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl7-" + Guid.NewGuid().ToString("N")[..12]);

    public EmOptimizationTests() => EmEvaluation.SolverStandIn = Stub();

    public void Dispose()
    {
        EmEvaluation.SolverStandIn = null;
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    // The coupling at 9.5 GHz inside a 0.2 dB band: reached on the circuit model near L90 = 5.0 mm, and on the stub —
    // whose lines are 3 % longer — at a shorter L90.
    private static OptimizationGoal Coupling(string lo = "-4.1", string hi = "-3.9") => new()
    {
        Name = "Coupling", Expression = "dB(SP1.S(3,1))", Analysis = "SP1", Type = GoalType.In, Limit = lo, UpperLimit = hi,
        Range = new GoalRange { Axis = "freq", Lo = "9.5 GHz", Hi = "9.5 GHz" },
    };

    private static TuningSetup Setup(OptimizerEmMode? em, string algorithm = "simplex", int? budget = null,
                                     string min = "4 mm", string max = "6 mm", OptimizationGoal? goal = null, string key = "L90")
        => new()
        {
            Variables = [new TunableEntry { Key = key, Opt = true, Min = min, Max = max, Scale = TuneScale.Lin }],
            Goals = [goal ?? Coupling()],
            Optimizer = new OptimizerSettings { Algorithm = algorithm, Em = em, EmBudget = budget, Seed = 1 },
        };

    /// <summary>A C-series design made EM-capable, its SP1 run by EM over 8-11 GHz, with <paramref name="setup"/>.</summary>
    private EmCellFixture Fixture(TuningSetup? setup, string design = "C2-branch-line", Action<SchematicEditModel>? edit = null,
                                  string tag = "")
    {
        var fx = EmCellFixture.Create(design, Path.Combine(_scratch, tag), AnalysisModel.Em, edit: edit);
        var (m, v, c) = SchematicPersistence.LoadFromFile(fx.Csch);
        int at = m.Analyses.FindIndex(a => a.Name == "SP1");
        m.Analyses[at] = new SParameterAnalysis("SP1", [new FrequencySpec("8", "11", 7, SweepKind.Linear, "GHz", "GHz")])
        { Model = AnalysisModel.Em };
        m.Tuning = setup;
        SchematicPersistence.SaveToFile(fx.Csch, m, c, v.PanX, v.PanY, v.Zoom);
        return fx;
    }

    private static OptimizationResult Run(EmCellFixture fx) => OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch)).Run();

    private static double Mm(IReadOnlyDictionary<string, string> values, string key)
    {
        Assert.True(TunableValue.TryParse(values[key], out _, out _, out double si), values[key]);
        return si * 1e3;
    }

    /// <summary>
    /// The stub EM solver: the schematic's own circuit model at its VAR values with every length 3 % longer and every
    /// width (a variable named w…) 10 µm wider, written as a Touchstone. <paramref name="lengthScale"/> 1 and
    /// <paramref name="widthAdd"/> 0 make EM agree with the circuit model exactly.
    /// </summary>
    internal static Func<EmCellGeometry, IReadOnlyDictionary<string, Value>, string, string?> Stub(double lengthScale = 1.03,
                                                                                                double widthAdd = 10e-6)
        => (g, values, pathWithoutExtension) =>
        {
            var model = SchematicPersistence.LoadFromFile(g.Target.SchematicPath).model;
            var tunables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters))
            {
                string name = row.Name.Trim();
                if (!values.TryGetValue(name, out var value) || value.Kind != ValueKind.Real) continue;
                double x = value.AsReal();
                x = name.StartsWith('w') ? x + widthAdd : x * lengthScale;
                tunables[name] = (x * 1e3).ToString("R", CultureInfo.InvariantCulture) + " mm";
            }
            var rr = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(g.Target.SchematicPath),
                new CircuitEvaluationRequest { Em = false, Tunables = tunables, Analyses = ["SP1"] });
            if (rr.Status != RunStatus.Success) return null;
            var snp = DataSetBuilder.ToSnp(rr.Results.Single(r => r.Name == "SP1").Data);
            string path = pathWithoutExtension + ".s" + snp.Ports.ToString(CultureInfo.InvariantCulture) + "p";
            TouchstoneIO.WriteFile(snp, path);
            return path;
        };

    /// <summary>
    /// D2: em=correct on C2 itself — goals on its own SP1 model=em — meets its goals on the stub within six solves, and
    /// lands within the goal's tolerance of where em=direct lands: both best points meet the 0.2 dB band on EM, at L90s
    /// closer together than the band spans (about 0.25 mm of L90 here).
    /// </summary>
    [Fact]
    public void Correct_MeetsItsGoalsWithinSixSolves_NearWhereDirectLands()
    {
        var correct = Run(Fixture(Setup(em: null), tag: "correct"));
        Assert.Equal(OptimizerEmMode.Correct, correct.EmMode);
        Assert.True(correct.Outcome == OptimizationOutcome.GoalsMet, correct.FinishReason + " " + string.Join(" | ", correct.Notes.Select(n => n.Render())));
        Assert.InRange(correct.EmSolves.Count(r => !r.Cached), 1, 6);

        var direct = Run(Fixture(Setup(OptimizerEmMode.Direct, algorithm: "pattern"), tag: "direct"));
        Assert.True(direct.Outcome == OptimizationOutcome.GoalsMet, direct.FinishReason);
        Assert.InRange(Math.Abs(Mm(correct.BestValues, "L90") - Mm(direct.BestValues, "L90")), 0, 0.25);
        // The circuit model alone meets the band at a longer L90, which EM does not accept.
        Assert.True(Mm(correct.BestValues, "L90") < 5.0, correct.BestValues["L90"]);
    }

    /// <summary>D4: emBudget=3 solves exactly three geometries, returns the best of them, and says it stopped by budget.</summary>
    [Fact]
    public void Budget_ThreeSolvesExactly_TheBestOfThemKept()
    {
        var r = Run(Fixture(Setup(OptimizerEmMode.Direct, algorithm: "pattern", budget: 3, goal: Coupling("-1", "-0.9"))));
        Assert.Equal(3, r.EmSolves.Count(s => !s.Cached));
        Assert.True(r.StoppedByBudget);
        Assert.Contains("EM budget of 3", r.FinishReason);
        Assert.Equal(OptimizationOutcome.GoalsUnmet, r.Outcome);
        var best = r.EmSolves.Where(s => !s.Failed).MinBy(s => s.Cost)!;
        Assert.Equal(best.Cost, r.BestCost);
    }

    /// <summary>D2's same-geometry stop: when EM agrees with the circuit model and the goal is out of reach, the corrected
    /// search proposes the point just solved, and the loop ends there with no second solve.</summary>
    [Fact]
    public void SameGeometry_EndsTheLoop_WithNoExtraSolve()
    {
        EmEvaluation.SolverStandIn = Stub(lengthScale: 1, widthAdd: 0);
        var r = Run(Fixture(Setup(em: null, goal: Coupling("-1", "-0.9"))));
        Assert.Single(r.EmSolves);
        Assert.Contains("same geometry", r.FinishReason);
    }

    /// <summary>R-sdl7-4: the values written back (BestValues — what Push and Apply write) are an EM-verified point:
    /// the best-scoring row of the run's EM solves, never a circuit-model point; and the history carries one row per solve.</summary>
    [Fact]
    public void WriteBack_IsTheBestEmVerifiedPoint()
    {
        var r = Run(Fixture(Setup(em: null)));
        var best = r.EmSolves.Where(s => !s.Failed).MinBy(s => s.Cost)!;
        Assert.Equal(best.Values, r.BestValues);
        Assert.Equal(best.Key, r.BestEmKey);
        Assert.Equal(r.EmSolves.Count, r.History!.CubesIn("opt")["em_cost"].Axes[0].Length);
    }

    /// <summary>
    /// R-sdl7-8: Apply after an optimisation on C2 — the VAR block holds the best values (still a VAR row, in its own
    /// unit), the saved layout's EM cache key is the best point's, and nothing else in the .csch changed.
    /// </summary>
    [Fact]
    public void Apply_WritesTheVarBlock_UpdatesTheLayoutToTheSolvedGeometry_AndNothingElse()
    {
        var fx = Fixture(Setup(em: null));
        var r = Run(fx);
        Assert.Equal(OptimizationOutcome.GoalsMet, r.Outcome);
        string before = File.ReadAllText(fx.Csch);

        var applied = EmApply.Apply(fx.Csch, r.BestValues, r.BestEmKey);
        Assert.True(applied.KeyMatches, applied.Sentence);
        Assert.Equal(["L90"], applied.Written);

        var after = SchematicPersistence.LoadFromFile(fx.Csch).model;
        var row = after.Components.Single(c => c.Symbol == SymbolKind.Var).Parameters.Single(p => p.Name == "L90");
        Assert.Equal("mm", row.Unit);
        Assert.Equal(Mm(r.BestValues, "L90"), double.Parse(row.Expression, CultureInfo.InvariantCulture), 9);

        // Nothing else: the old file with only that row put back reads the same.
        var (old, view, cell) = SchematicPersistence.LoadFromFile(fx.Csch);
        var reverted = SchematicPersistence.Deserialize(before).model;
        var oldRow = reverted.Components.Single(c => c.Symbol == SymbolKind.Var).Parameters.Single(p => p.Name == "L90");
        (oldRow.Expression, oldRow.Unit) = (row.Expression, row.Unit);
        Assert.Equal(SchematicPersistence.Serialize(reverted, cell, view.PanX, view.PanY, view.Zoom),
                     SchematicPersistence.Serialize(old, cell, view.PanX, view.PanY, view.Zoom));
    }

    /// <summary>
    /// D9/D10 on C2: a range for L90 reaching down to where the short arms make ML3 cross ML4 and the tees. A goal that
    /// pulls toward short arms (S11 at 9.5 GHz falls as L90 grows) drives the corrected search into that region: its
    /// points are infeasible, cost no solve and each says why; the range scan reports the connected interval exactly
    /// where the geometry itself stops overlapping; and every geometry EM solves overlaps nothing.
    /// </summary>
    [Fact]
    public void Connectivity_OverlappingPointsAreInfeasible_UnsolvedAndExplained_AndTheScanFindsTheEdge()
    {
        var pullShort = new OptimizationGoal
        {
            Name = "Short", Expression = "dB(SP1.S(1,1))", Analysis = "SP1", Type = GoalType.Ge, Limit = "-4",
            Range = new GoalRange { Axis = "freq", Lo = "9.5 GHz", Hi = "9.5 GHz" },
        };
        var fx = Fixture(Setup(em: null, budget: 3, min: "0.05 mm", max: "6 mm", goal: pullShort));
        var r = Run(fx);

        var range = Assert.Single(r.ConnectedRanges);
        Assert.False(range.Whole);
        // Exactly: the scan's lower edge is the first of its 64 points whose own geometry overlaps nothing.
        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;
        double first = Enumerable.Range(0, OptimizationRun.ScanPoints)
            .Select(j => (0.05 + j / (double)(OptimizationRun.ScanPoints - 1) * (6 - 0.05)) * 1e-3)
            .First(v => EmEvaluation.Geometry(target, new Dictionary<string, Value> { ["L90"] = new(v) }, out _)!.Connectivity!.Touching.Count == 0);
        Assert.Equal(first * 1e3, range.Lo, 6);
        Assert.Equal(6, range.Hi, 9);
        Assert.Contains($"L90: connected from {TunableValue.Format(range.Lo, "mm", "G6")} to 6 mm", range.Sentence);

        var infeasible = r.Log.Where(l => l.Infeasible).ToList();
        Assert.NotEmpty(infeasible);
        Assert.All(infeasible, l => Assert.Matches("^L90 = .*: ML\\d (overlaps|touches) ", l.Why!));
        // Every solved geometry overlaps nothing. (Not "lies inside the scanned interval": the scan is a 64-point grid, and
        // the search may find connected copper between its last overlapping point and its first clean one.)
        Assert.NotEmpty(r.EmSolves);
        Assert.All(r.EmSolves, s => Assert.Empty(EmEvaluation.Geometry(target,
            new Dictionary<string, Value> { ["L90"] = new(Mm(s.Values, "L90") * 1e-3) }, out _)!.Connectivity!.Touching));
    }

    /// <summary>
    /// D9's gap by design: H2's middle coupled section, its spacing a VAR whose range reaches below zero. A spacing at or
    /// below zero joins the two strips the section keeps apart, so no such point is ever simulated; the run optimises
    /// the spacing everywhere above it.
    /// </summary>
    [Fact]
    public void CoupledLineSpacing_NeverEvaluatedAtOrBelowZero()
    {
        var fx = Fixture(Setup(OptimizerEmMode.Direct, algorithm: "pattern", budget: 4, min: "-0.3 mm", max: "0.6 mm", key: "S2",
                               goal: new OptimizationGoal
                               {
                                   Name = "Pass", Expression = "dB(SP1.S(2,1))", Analysis = "SP1", Type = GoalType.Ge, Limit = "-1",
                                   Range = new GoalRange { Axis = "freq", Lo = "9.5 GHz", Hi = "9.5 GHz" },
                               }),
                         design: "H2-coupled-bandpass", edit: m =>
                         {
                             m.Components.Add(new EditableComponent
                             {
                                 InstanceName = "VAR1", Symbol = SymbolKind.Var,
                                 Parameters = { new EditableParameter { Name = "S2", Expression = "0.4", Unit = "mm" } },
                             });
                             var s = m.Components.Single(c => c.InstanceName == "CL2").Parameters.Single(p => p.Name == "S");
                             (s.Expression, s.Unit) = ("S2", "");
                         });
        var r = Run(fx);
        Assert.NotEqual(OptimizationOutcome.Refused, r.Outcome);
        Assert.Contains(r.ConnectedRanges, c => c.Key == "S2" && c.Lo > 0);
        Assert.All(r.Log.Where(l => !l.Infeasible), l => Assert.True(Mm(l.Decoded.Values, "S2") > 0, l.Decoded.Values["S2"]));
        Assert.All(r.EmSolves, s => Assert.True(Mm(s.Values, "S2") > 0, s.Values["S2"]));
    }

    /// <summary>D9: a start whose copper already has a loop gap (C6: ML4 50 µm too long) is refused before any solve,
    /// quoting the planner's report.</summary>
    [Fact]
    public void StartWithALoopGap_IsRefused_BeforeAnySolve()
    {
        var r = Run(Fixture(Setup(em: null), design: "C6-broken-loop"));
        Assert.Equal(OptimizationOutcome.Refused, r.Outcome);
        Assert.Equal("opt.em.start-connectivity", r.Refusal!.Id);
        Assert.Contains("does not close", r.FinishReason);
        Assert.Empty(r.EmSolves);
    }

    /// <summary>D3: em=direct with a population algorithm is refused naming the accepted ones — by the run and by check's
    /// validator alike.</summary>
    [Fact]
    public void Direct_WithAPopulationAlgorithm_IsRefused()
    {
        var fx = Fixture(Setup(OptimizerEmMode.Direct, algorithm: "cmaes"));
        var run = OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch));
        Assert.Equal("tuning.optimize.em-direct-algorithm", run.Refusal!.Id);
        Assert.Contains("pattern, simplex, trust_region, bayes", run.Refusal.Render());
    }

    /// <summary>D6: with ML3's width set in the layout, w50 no longer reaches it — SDL-6 D4's finding — and optimising w50
    /// by EM is refused rather than spent.</summary>
    [Fact]
    public void AVariableThatDoesNotReachTheCopper_IsRefused()
    {
        var fx = Fixture(Setup(em: null, min: "250 um", max: "320 um", key: "w50"));
        var layout = LayoutPersistence.LoadFromFile(fx.P.ClayPath);
        var ml3 = layout.Instances.Single(i => i.SchematicId == "ML3");
        var origin = CellLayoutResolver.Resolve(ml3.CellRef, fx.P.LayoutDir).View!.PCellOrigin!;
        string cell = GeneratedCellStore.GetOrCreate(fx.Workspace, "MLIN",
            new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 300e-6 }, fx.P.Tech, fx.P.TechPath, PCellLayerSelection.Default);
        ml3.CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(fx.P.LayoutDir, cell));
        LayoutPersistence.SaveToFile(fx.P.ClayPath, layout);

        var r = Run(fx);
        Assert.Equal(OptimizationOutcome.Refused, r.Outcome);
        Assert.Equal("opt.em.unreached", r.Refusal!.Id);
        Assert.Contains("w50 does not reach ML3.W", r.FinishReason);
    }

    /// <summary>D8: a yield run whose spec reads an analysis run by EM is refused with its trial count and the way to run it
    /// by circuit models; run by circuit models, it is not refused for EM.</summary>
    [Fact]
    public void Yield_OnAnAnalysisRunByEm_IsRefused_AndRunsByCircuitModels()
    {
        var setup = Setup(em: null);
        setup.Variables[0].Stat = true;
        setup.Variables[0].Distribution = StatDistribution.Gauss;
        setup.Variables[0].Spread = new StatSpread { Sd = "1%" };
        setup.Goals[0].Use = GoalUse.Yield;
        setup.Statistics = new StatisticsSettings { Trials = 4 };
        var fx = Fixture(setup);

        var byEm = StatisticalRun.Create(PreparedCircuit.FromSchematic(fx.Csch)).Run();
        Assert.Equal("yield.run.em", byEm.Refusal?.Id);
        Assert.Contains("4 evaluations", byEm.FinishReason);
        Assert.Contains("--em off", byEm.FinishReason);

        var byCircuit = StatisticalRun.Create(PreparedCircuit.FromSchematic(fx.Csch), new StatisticalOptions { Em = false }).Run();
        Assert.True(byCircuit.Refusal is null, byCircuit.Refusal?.Render());
    }

    /// <summary>R-sdl7-1: em= and emBudget= round-trip the optimize line, and check's validator refuses a budget below 1.</summary>
    [Fact]
    public void Grammar_EmAndBudget_RoundTrip_AndABudgetBelowOneIsAnError()
    {
        var setup = new TuningSetup { Optimizer = new OptimizerSettings { Algorithm = "pattern", Em = OptimizerEmMode.Direct, EmBudget = 7 } };
        string line = TuningDirectiveText.Write(setup).Single();
        Assert.Equal("optimize algorithm=pattern em=direct emBudget=7", line);
        var bench = new TestBench("tb");
        var warnings = new List<string>();
        TuningDirectiveText.Read("optimize", line["optimize ".Length..], bench, warnings, 1);
        Assert.Empty(warnings);
        Assert.Equal((OptimizerEmMode?)OptimizerEmMode.Direct, bench.Tuning!.Optimizer!.Em);
        Assert.Equal(7, bench.Tuning.Optimizer.EmBudget);

        var tb = new TestBench("tb") { Tuning = new TuningSetup { Optimizer = new OptimizerSettings { EmBudget = 0 } } };
        var f = TuningValidator.Validate(tb, TunableCatalog.FromNetlist(tb, new Library("l")));
        Assert.Contains(f, d => d.Id == "tuning.optimize.em-budget");
    }

    // ── Review fixes ─────────────────────────────────────────────────────────

    /// <summary>The stub, counting the solves it is asked for.</summary>
    private static Func<EmCellGeometry, IReadOnlyDictionary<string, Value>, string, string?> Counting(
        StrongBox<int> calls, Func<EmCellGeometry, IReadOnlyDictionary<string, Value>, string, string?>? inner = null)
    {
        var solve = inner ?? Stub();
        return (g, values, path) => { Interlocked.Increment(ref calls.Value); return solve(g, values, path); };
    }

    private static OptimizationGoal At95(string name, string expression, string analysis, GoalType type, string limit) => new()
    {
        Name = name, Expression = expression, Analysis = analysis, Type = type, Limit = limit,
        Range = new GoalRange { Axis = "freq", Lo = "9.5 GHz", Hi = "9.5 GHz" },
    };

    private static void EditSchematic(EmCellFixture fx, Action<SchematicEditModel> edit)
    {
        var (m, v, c) = SchematicPersistence.LoadFromFile(fx.Csch);
        edit(m);
        SchematicPersistence.SaveToFile(fx.Csch, m, c, v.PanX, v.PanY, v.Zoom);
    }

    private static EditableParameter L90Row(SchematicEditModel m) =>
        m.Components.Single(c => c.Symbol == SymbolKind.Var).Parameters.Single(p => p.Name == "L90");

    /// <summary>D1/D8: analyses=all simulates SP1 (run by EM) though no goal reads it; em=off runs it by circuit models, so
    /// the run makes no EM solve at all.</summary>
    [Fact]
    public void EmOff_WithAnalysesAll_SolvesNothingByEm()
    {
        var setup = Setup(OptimizerEmMode.Off, goal: At95("Thru", "dB(SP2.S(2,1))", "SP2", GoalType.Ge, "-3"));
        setup.Optimizer!.Scope = OptimizerScope.All;
        setup.Optimizer.MaxIterations = 3;
        var fx = Fixture(setup);
        EditSchematic(fx, m => m.Analyses.Add(new SParameterAnalysis("SP2", [new FrequencySpec("8", "11", 7, SweepKind.Linear, "GHz", "GHz")])));
        var calls = new StrongBox<int>();
        EmEvaluation.SolverStandIn = Counting(calls);

        var run = OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch));
        var r = run.Run();
        Assert.NotEqual(OptimizationOutcome.Refused, r.Outcome);
        Assert.False(run.EvaluatesEm);
        Assert.Equal(0, calls.Value);
    }

    /// <summary>D2: while em=correct's circuit-model searches run, the best a caller is shown (what Push may write while
    /// paused) is an EM-verified point, or none before the first solve — never the circuit model's own best.</summary>
    [Fact]
    public void Correct_ProgressShowsOnlyEmVerifiedBestValues()
    {
        var fx = Fixture(Setup(em: null));
        var seen = new List<IReadOnlyDictionary<string, string>>();
        var r = OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch),
            new OptimizationOptions { Progress = p => { lock (seen) seen.Add(p.BestValues); } }).Run();
        Assert.Equal(OptimizerEmMode.Correct, r.EmMode);
        Assert.NotEmpty(seen);
        Assert.All(seen.Where(v => v.Count > 0), v => Assert.Contains(r.EmSolves, s => s.Values.SequenceEqual(v)));
    }

    /// <summary>R-sdl7-8: a cell with two EM setups, its analysis naming one. The result names the setup that solved it, and
    /// Apply given it keys the saved layout by that setup — without it, the cell names none by itself.</summary>
    [Fact]
    public void Apply_OnACellWithTwoSetups_UsesTheOneTheRunSolvedWith()
    {
        var fx = Fixture(Setup(em: null));
        File.Copy(fx.Cem, Path.Combine(Path.GetDirectoryName(fx.Cem)!, "C2-second" + Path.GetExtension(fx.Cem)));
        EditSchematic(fx, m => m.Analyses.Single(a => a.Name == "SP1").EmSetup = Path.GetFileNameWithoutExtension(fx.Cem));
        Assert.Null(EmCapability.Resolve(fx.P.CellDir, null, null, out _));

        var r = Run(fx);
        Assert.NotNull(r.EmSetup);
        var applied = EmApply.Apply(fx.Csch, r.BestValues, r.BestEmKey, r.EmSetup);
        Assert.True(applied.KeyMatches, applied.Sentence);
    }

    /// <summary>R-sdl7-4: an em=off run on an analysis run by EM, its best point re-evaluated as the run evaluated
    /// (<see cref="OptimizationRun.EvaluatesEm"/>, what the CLI's -o and the Optimizer's plots pass): no EM solve.</summary>
    [Fact]
    public void EmOff_BestPointReEvaluatedAsTheRunEvaluated_SolvesNothing()
    {
        var setup = Setup(OptimizerEmMode.Off);
        setup.Optimizer!.MaxIterations = 3;
        var fx = Fixture(setup);
        var calls = new StrongBox<int>();
        EmEvaluation.SolverStandIn = Counting(calls);

        var run = OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch));
        var r = run.Run();
        var rr = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch),
            new CircuitEvaluationRequest { Tunables = r.BestValues, Em = run.EvaluatesEm });
        Assert.Equal(RunStatus.Success, rr.Status);
        Assert.Equal(0, calls.Value);
    }

    /// <summary>D2 under the run's limits: maxiter=1 spans the whole correct loop — one EM solve at the circuit model's
    /// first proposal, then the limit — rather than every inner search getting its own.</summary>
    [Fact]
    public void Correct_TheRunsIterationLimit_SpansTheWholeLoop()
    {
        var setup = Setup(em: null, goal: Coupling("-1", "-0.9"));
        setup.Optimizer!.MaxIterations = 1;
        var r = Run(Fixture(setup));
        Assert.Single(r.EmSolves);
        Assert.Contains("iteration limit", r.FinishReason);
    }

    /// <summary>D10: L90's range runs from deep in the overlap to just past the edge where the arms stop overlapping, so its
    /// connected stretch is narrower than one of the scan's 64 steps. It still reaches the copper: it is scanned and
    /// reported (the start alone), not refused as having no effect.</summary>
    [Fact]
    public void ANarrowConnectedStretch_IsReported_NotRefusedAsNoEffect()
    {
        var edgeFx = Fixture(null, tag: "edge");
        var target = EmCapability.Resolve(edgeFx.P.CellDir, null, null, out _)!;
        bool Clean(double mm) => EmEvaluation.Geometry(target, new Dictionary<string, Value> { ["L90"] = new(mm * 1e-3) }, out _)!
                                     .Connectivity!.Disagreement is null;
        double lo = 0.05, hi = 6;
        Assert.False(Clean(lo));
        while (hi - lo > 1e-3) { double mid = (lo + hi) / 2; if (Clean(mid)) hi = mid; else lo = mid; }

        string max = hi.ToString("R", CultureInfo.InvariantCulture) + " mm";
        var r = Run(Fixture(Setup(em: null, budget: 1, min: "0.05 mm", max: max), tag: "narrow"));
        Assert.NotEqual("opt.em.no-effect", r.Refusal?.Id);
        var range = Assert.Single(r.ConnectedRanges);
        Assert.False(range.Whole);
        Assert.Equal(hi, range.Hi, 9);
    }

    /// <summary>D9: the schematic's own L90 overlaps (0.05 mm, written after its layout was made at the old value), and the
    /// tuned range clamps the start to 4 mm, which does not. The start is checked at the values the run solves, so it is
    /// not refused for the schematic's.</summary>
    [Fact]
    public void TheStartIsCheckedAtItsClampedValues_NotTheSchematicsSaved()
    {
        var fx = Fixture(Setup(em: null, budget: 1, min: "4 mm", max: "6 mm"));
        EditSchematic(fx, m => (L90Row(m).Expression, L90Row(m).Unit) = ("0.05", "mm"));
        var r = Run(fx);
        Assert.NotEqual("opt.em.start-connectivity", r.Refusal?.Id);
        Assert.NotEmpty(r.EmSolves);
    }

    /// <summary>A Tuning panel on <paramref name="fx"/>'s schematic, L90 ticked Tune over [min, max], its session started on
    /// the design's own circuit.</summary>
    private static (TuningPanelViewModel Panel, SchematicViewModel Vm, TuningRowViewModel Row) TuningOn(
        EmCellFixture fx, string min, string max)
    {
        var model = SchematicPersistence.LoadFromFile(fx.Csch).model;
        model.SchematicDirectory = Path.GetDirectoryName(fx.Csch);
        model.Tuning = new TuningSetup
        {
            Variables = [new TunableEntry { Key = "L90", Tune = true, Min = min, Max = max, Scale = TuneScale.Lin }],
        };
        var vm = new SchematicViewModel(model);
        var panel = new TuningPanelViewModel();
        panel.SetActiveSchematic(vm, Path.GetFileName(fx.Csch));
        panel.CreateSession = (_, analyses) => TuneSession.ForCircuit(PreparedCircuit.FromSchematic(fx.Csch), null, a => a(), analyses);
        panel.StartCommand.Execute(null);
        Assert.NotNull(panel.Session);
        Assert.True(panel.HasEm);
        return (panel, vm, panel.Rows.Single(r => r.Key == "L90"));
    }

    /// <summary>D7: a slider moved while Solve EM here is solving leaves the solve's traces stale when it lands.</summary>
    [Fact]
    public async Task SolveEmHere_ASliderMovedDuringTheSolve_LeavesItsTracesStale()
    {
        var (panel, _, row) = TuningOn(Fixture(null), "4 mm", "6 mm");
        var stub = Stub();
        EmEvaluation.SolverStandIn = (g, values, path) =>
        {
            row.ValueBoxText = "4.31";
            row.CommitValueText();
            return stub(g, values, path);
        };
        await panel.SolveEmHereCommand.ExecuteAsync(null);
        Assert.True(panel.EmStale, panel.EmText);
        panel.StopCommand.Execute(null);
    }

    /// <summary>D7: a tuned value replaced from outside the panel (an edit of the schematic, which the slider follows) marks
    /// the EM traces stale as a drag does.</summary>
    [Fact]
    public async Task SolveEmHere_AValueTheSliderFollowsFromTheSchematic_LeavesItsTracesStale()
    {
        var (panel, vm, row) = TuningOn(Fixture(null), "4 mm", "6 mm");
        row.ValueBoxText = "4.31";
        row.CommitValueText();
        await panel.SolveEmHereCommand.ExecuteAsync(null);
        Assert.False(panel.EmStale, panel.EmText);

        L90Row(vm.EditModel).Expression = "4.69";
        vm.EditModel.NotifyChanged();
        Assert.True(panel.EmStale, panel.EmText);
        panel.StopCommand.Execute(null);
    }

    /// <summary>D10 in the Tuning panel: each slider shows the connected interval the optimizer's own scan finds — on C2,
    /// L90's lower edge is the first of the 64 points whose copper overlaps nothing.</summary>
    [Fact]
    public void Tuning_ShowsTheSlidersConnectedInterval()
    {
        var fx = Fixture(null);
        var (panel, _, row) = TuningOn(fx, "0.05 mm", "6 mm");
        Assert.True(SpinWait.SpinUntil(() => row.Connected is not null, TimeSpan.FromSeconds(60)), panel.ConnectivityText);

        var target = EmCapability.Resolve(fx.P.CellDir, null, null, out _)!;
        double first = Enumerable.Range(0, OptimizationRun.ScanPoints)
            .Select(j => (0.05 + j / (double)(OptimizationRun.ScanPoints - 1) * (6 - 0.05)) * 1e-3)
            .First(v => EmEvaluation.Geometry(target, new Dictionary<string, Value> { ["L90"] = new(v) }, out _)!.Connectivity!.Touching.Count == 0);
        Assert.Equal(first * 1e3, row.Connected!.Lo, 6);
        Assert.Equal(6, row.Connected.Hi, 9);
        Assert.StartsWith("connected ", row.ConnectedText);
        Assert.True(panel.HasConnectivity);
        panel.StopCommand.Execute(null);
    }

    /// <summary>D10 in the Tuning panel: a value moved outside its interval is taken (the slider is never held), and the
    /// status line names what the copper does there in the planner's words.</summary>
    [Fact]
    public void Tuning_AValueOutsideItsInterval_IsTaken_AndTheStatusLineNamesTheOverlap()
    {
        var (panel, _, row) = TuningOn(Fixture(null), "0.05 mm", "6 mm");
        Assert.True(SpinWait.SpinUntil(() => row.Connected is not null, TimeSpan.FromSeconds(60)), panel.ConnectivityText);

        row.ValueBoxText = "0.05";
        row.CommitValueText();
        Assert.Equal(0.05, row.Value, 9);
        Assert.True(row.IsOutsideConnected);
        Assert.True(SpinWait.SpinUntil(() => panel.ConnectivityText.StartsWith("Not connected", StringComparison.Ordinal),
                                       TimeSpan.FromSeconds(60)), panel.ConnectivityText);
        Assert.Matches("ML\\d (overlaps|touches) ", panel.ConnectivityText);
        panel.StopCommand.Execute(null);
    }

    /// <summary>R-sdl7-8: the Optimizer's Push writes an EM-verified best as it was solved, not rounded to the panel's
    /// digits (two here), which could put it on a neighbouring geometry.</summary>
    [Fact]
    public async Task OptimizerPush_WritesTheEmVerifiedBest_Unrounded()
    {
        var setup = Setup(em: null);
        setup.Digits = 2;
        var fx = Fixture(setup);
        var model = SchematicPersistence.LoadFromFile(fx.Csch).model;
        model.SchematicDirectory = Path.GetDirectoryName(fx.Csch);
        var vm = new SchematicViewModel(model);
        var panel = new OptimizerPanelViewModel { PrepareCircuit = _ => PreparedCircuit.FromSchematic(fx.Csch) };
        panel.SetActiveSchematic(vm, Path.GetFileName(fx.Csch));
        panel.RunCommand.Execute(null);
        await panel.RunTask!.WaitAsync(TimeSpan.FromMinutes(2));

        var best = panel.Result!.BestValues;
        Assert.NotEqual(best["L90"], TuningDigits.Round(best, 2)["L90"]);
        panel.PushCommand.Execute(null);
        var row = L90Row(model);
        Assert.True(TunableValue.TryParse(row.Expression + " " + row.Unit, out _, out _, out double written), row.Expression);
        Assert.Equal(Mm(best, "L90") * 1e-3, written, 12);
    }

    /// <summary>D4: a failed EM solve is charged every time it is attempted — the solves asked of the solver never exceed
    /// the budget, and the run counts each one.</summary>
    [Fact]
    public void FailedSolves_AreChargedEachAttempt_AndNeverExceedTheBudget()
    {
        var calls = new StrongBox<int>();
        EmEvaluation.SolverStandIn = Counting(calls, (_, _, _) => null);
        var r = Run(Fixture(Setup(OptimizerEmMode.Direct, algorithm: "pattern", budget: 3)));
        Assert.InRange(calls.Value, 1, 3);
        Assert.Equal(calls.Value, r.EmBudgetUsed);
    }

    /// <summary>D4: the budget in force is reported once the start is checked and before the first solve — what the CLI
    /// prints up front, now that a 3D EM view's lower default is known only then.</summary>
    [Fact]
    public void TheBudgetInForce_IsReportedBeforeTheFirstSolve()
    {
        var fx = Fixture(Setup(OptimizerEmMode.Direct, algorithm: "pattern", budget: 2));
        var calls = new StrongBox<int>();
        EmEvaluation.SolverStandIn = Counting(calls);
        (OptimizerEmMode Mode, int Budget, int SolvesSoFar)? said = null;
        OptimizationRun.Create(PreparedCircuit.FromSchematic(fx.Csch),
            new OptimizationOptions { EmBudgetKnown = (mode, budget) => said = (mode, budget, calls.Value) }).Run();
        Assert.NotNull(said);
        Assert.Equal((OptimizerEmMode.Direct, 2, 0), said.Value);
    }
}
