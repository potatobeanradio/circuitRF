using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.Loadpull;
using CircuitRF.Ui.Tests.Statistics;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// LP-1 — a loadpull's grid runs one load per core by default; <c>Workers=</c> caps it from the GUI as from a netlist, and
/// an outer loop that already runs evaluations side by side (the optimizer, yield) pins each point's grid to the serial
/// loop, so points × cores threads and netlist copies never happen.
/// </summary>
public sealed class LoadpullWorkersTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("crf-lpw-").FullName;

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    /// <summary>Hero 3 on four loads and six rungs, plus <paramref name="extra"/> lines.</summary>
    private string Bench(string extra = "")
    {
        string dir = Path.GetDirectoryName(FixturePaths.Find("testdata/Hero3/hero3.cnl")!)!;
        string gam = Path.Combine(_tmp, "g.gam");
        File.WriteAllLines(gam, ["# gamma Z0=50 mag_ang", "0 0", "0.2 0", "0.2 90", "0.2 180"]);
        return File.ReadAllText(Path.Combine(dir, "hero3.cnl"))
                   .Replace("Grid=\"hero3_load.gam\"", $"Grid=\"{gam}\"").Replace("PinMax=10", "PinMax=-15") + "\n" + extra;
    }

    [Fact]
    public void AnEvaluationRequest_CapsTheGrid_SweptOrNot()
    {
        var circuit = PreparedCircuit.FromText(Bench(), null, null);
        Assert.Equal(RunStatus.Success, CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest()).Status);
        Assert.True(LoadpullEngine.LastLoadpullDegree > 1);
        Assert.Equal(RunStatus.Success, CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { LoadpullWorkers = 1 }).Status);
        Assert.Equal(1, LoadpullEngine.LastLoadpullDegree);

        var swept = PreparedCircuit.FromText(Bench("analysis SW1 type=parametric_sweep Var=RFfreq Values=1.9e9,2.1e9 Inner=LP1"), null, null);
        var r = CircuitEvaluation.Evaluate(swept, new CircuitEvaluationRequest { LoadpullWorkers = 1, Analyses = ["SW1"] });
        Assert.Equal(RunStatus.Success, r.Status);
        Assert.Equal(1, LoadpullEngine.LastLoadpullDegree);
    }

    /// <summary>A yield run four trials at a time: every trial's loadpull ran serially. The calling thread is one of the
    /// four, so the thread-static degree it reads is a trial's.</summary>
    [Fact]
    public void InsideAParallelYieldRun_EachTrialsGridRunsSerially()
    {
        var run = YieldCircuits.Create(Bench("""
            R:Rx n_gate 0 R=1e6 Ohm
            tune Rx.R dist=gauss sd=1%
            goal S = max_over(LP1.StopCode, "gridPoint") analysis=LP1 le 3 use=yield
            statistics trials=8 parallel=4
            """));
        LoadpullEngine.LastLoadpullDegree = 0;
        var r = run.Run();
        Assert.True(r.Trials == 8, $"{r.Outcome}: {r.FinishReason} {string.Join(" / ", r.Notes.Select(n => n.Render()))}");
        Assert.Equal(1, LoadpullEngine.LastLoadpullDegree);
    }

    /// <summary><c>Workers=</c> survives the dialog and the <c>.csch</c>, and a default one writes nothing new.</summary>
    [Fact]
    public void TheWorkersKey_RoundTripsThroughTheDialogAndTheSchematic()
    {
        var model = new SchematicEditModel();
        var lp = new LoadpullAnalysis("LP1") { WorkersExpr = "3" };
        var vm = LpBodyViewModel.FromAnalysis(lp, model);
        Assert.Equal(3, vm.Workers);
        Assert.Equal("3", vm.BuildAnalysis("LP1", true).WorkersExpr);
        Assert.Equal("3", LppBodyViewModel.FromAnalysis(new LoadpullPursuitAnalysis("LPP1") { WorkersExpr = "3" }, model)
                                          .BuildAnalysis("LPP1", true).WorkersExpr);

        var dto = AnalysisSerialization.ToDto(lp)!;
        Assert.Equal("3", dto.LpWorkersExpr);
        Assert.Equal("3", Assert.IsType<LoadpullAnalysis>(AnalysisSerialization.FromDto(dto)).WorkersExpr);
        Assert.Null(AnalysisSerialization.ToDto(new LoadpullAnalysis("LP1"))!.LpWorkersExpr);
        var lpp = new LoadpullPursuitAnalysis("LPP1") { WorkersExpr = "2" };
        Assert.Equal("2", Assert.IsType<LoadpullPursuitAnalysis>(AnalysisSerialization.FromDto(AnalysisSerialization.ToDto(lpp)!)).WorkersExpr);
    }
}
