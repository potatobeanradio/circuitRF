using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Ui.Tests.SchematicSync;
using Xunit;

namespace CircuitRF.Ui.Tests.Transient;

/// <summary>
/// brief-trn-4 R-trn4-5 — model=em on a tran analysis runs the EM-capable cell through its cached EM result, selected as an
/// S-parameter run selects it, and that Touchstone is realized by the wideband fit (its note says so).
/// </summary>
public sealed class TransientEmCellTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-trn4em-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ModelEm_OnATranAnalysis_RunsTheCachedEmResult_ThroughTheFit()
    {
        var fx = EmCellFixture.Create("C2-branch-line", _scratch, AnalysisModel.Circuit, edit: m =>
            m.Analyses.Add(new TransientAnalysis("TR1") { StopExpr = "1n", StepExpr = "0.01n", Model = AnalysisModel.Em }));
        int solves = 0;
        EmEvaluation.SolverStandIn = (_, _, path) =>
        {
            Interlocked.Increment(ref solves);
            File.WriteAllText(path + ".s4p", FourPort(0.1));
            return path + ".s4p";
        };
        try
        {
            var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(fx.Csch),
                                                 new CircuitEvaluationRequest { Analyses = ["TR1"], EmMaxSolves = 1 });
            Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
            Assert.Equal(1, solves);
            Assert.Contains(run.Results, r => r.Name == "TR1" && r.Data.Contains("V"));
            Assert.Contains(run.Notes.Concat(run.Warnings), n => n.Contains("rational fit") && n.Contains("its data stops at 3 GHz"));
        }
        finally { EmEvaluation.SolverStandIn = null; }
    }

    private static string FourPort(double s21)
    {
        var sb = new StringBuilder("# GHz S RI R 50\n");
        foreach (int f in new[] { 1, 2, 3 })
            for (int i = 0; i < 4; i++)
            {
                sb.Append(i == 0 ? f.ToString(CultureInfo.InvariantCulture) : " ");
                for (int j = 0; j < 4; j++)
                    sb.Append(' ').Append((i != j ? s21 : 0.0).ToString("R", CultureInfo.InvariantCulture)).Append(" 0");
                sb.Append('\n');
            }
        return sb.ToString();
    }
}
