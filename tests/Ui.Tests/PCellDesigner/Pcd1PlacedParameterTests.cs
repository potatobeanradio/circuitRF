// Owner: PCD-1 (brief-pcd-1 follow-up: a placed designed cell's own parameter is an EM tunable).

using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd1PlacedParameterTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd1p-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        EmEvaluation.SolverStandIn = null;
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>The bench by EM of its own layout: <c>tune X1.Lpad</c> is accepted, and X1.Lpad moves the geometry.</summary>
    [Fact]
    public void APlacedDesignedCellsOwnParameter_ReachesTheCopper()
    {
        string ws = Pcd8PadTee.Workspace(_scratch);
        string bench = Pcd8PadTee.Bench(ws, Pcd8PadTee.Design(ws));
        var laid = LayoutFromSchematic.Run(bench, emSetup: true);
        Assert.True(laid.Refusal is null, laid.Refusal);

        var (m, v, c) = SchematicPersistence.LoadFromFile(bench);
        int sp = m.Analyses.FindIndex(a => a.Name == "SP1");
        m.Analyses[sp] = new SParameterAnalysis("SP1", [new FrequencySpec("6", "8", 3, SweepKind.Linear, "GHz", "GHz")]) { Model = AnalysisModel.Em };
        m.Tuning = new TuningSetup
        {
            Variables = [new TunableEntry { Key = "X1.Lpad", Opt = true, Min = "100 um", Max = "600 um", Scale = TuneScale.Lin }],
            Goals = [new OptimizationGoal { Name = "Match", Expression = "dB(SP1.S(1,1))", Analysis = "SP1", Type = GoalType.Le, Limit = "-15" }],
            Optimizer = new OptimizerSettings { Algorithm = "simplex", Em = OptimizerEmMode.Correct, EmBudget = 1, Seed = 1 },
        };
        SchematicPersistence.SaveToFile(bench, m, c, v.PanX, v.PanY, v.Zoom);

        // The geometry handed to the solver changes with X1.Lpad.
        string Key(string lpad)
        {
            var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(bench),
                new CircuitEvaluationRequest { Tunables = new Dictionary<string, string> { ["X1.Lpad"] = lpad } });
            Assert.True(plan.Status == RunStatus.Success, plan.StatusMessage);
            var count = CircuitEvaluation.CountEmSolves(plan, estimate: false);
            Assert.Empty(count.Refusals);
            return Assert.Single(count.Records).Key!;
        }
        Assert.Equal(Key("200 um"), Key("200 um"));
        Assert.NotEqual(Key("200 um"), Key("400 um"));

        // And the optimisation accepts it.
        EmEvaluation.SolverStandIn = (g, values, path) =>
        {
            var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(bench), new CircuitEvaluationRequest { Em = false, Analyses = ["SP1"] });
            string written = path + ".s2p";
            TouchstoneIO.WriteFile(DataSetBuilder.ToSnp(r.Results.Single(a => a.Name == "SP1").Data), written);
            return written;
        };
        var run = OptimizationRun.Create(PreparedCircuit.FromSchematic(bench)).Run();
        Assert.True(run.Refusal is null, run.Refusal?.Render());
        Assert.Equal(OptimizerEmMode.Correct, run.EmMode);
    }
}
