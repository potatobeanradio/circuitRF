using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using RfCore;
using RfCore.Data;
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
        Assert.Contains($"L90: connected from {TunableValue.Format(range.Lo, "mm")} to 6 mm", range.Sentence);

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
}
