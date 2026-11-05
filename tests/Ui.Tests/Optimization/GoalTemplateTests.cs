using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// brief-tuneopt-9 R-to9-3: every template family writes an expression that evaluates on the results
/// of a circuit it was offered for — through the same <see cref="CircuitEvaluation"/> the optimizer
/// evaluates goals with.
/// </summary>
public sealed class GoalTemplateTests
{
    [Fact]
    public void SParameterAndMeasurementTemplates_EvaluateOnTheAmplifierExample()
    {
        var circuit = PreparedCircuit.FromSchematic(
            Path.Combine(RepoRoot(), "examples", "S-Parameters", "Amplifier", "schematic", "Amplifier.csch"));
        Assert.Null(circuit.ReadError);
        var templates = GoalTemplates.For(circuit);

        var sp = templates.Where(t => t.Group == GoalTemplateGroup.SParameters).ToList();
        Assert.Equal(["SP1.sij", "SP1.phase", "SP1.group_delay", "SP1.vswr", "SP1.mu", "SP1.mu_prime", "SP1.K", "SP1.max_gain"],
                     sp.Select(t => t.Id));
        var meas = templates.Where(t => t.Group == GoalTemplateGroup.Measurements).ToList();
        Assert.Contains(meas, t => t.Id == "meas.K");
        Assert.All(meas, t => Assert.Equal("SP1", t.Make().Analysis));   // found through the measure's own text

        var goals = sp.Concat(meas).SelectMany(Variants).ToList();
        var values = Evaluate(circuit, goals);

        // The example computes Rollett K by hand as a measure; the template's K(SP1.S) must agree.
        var k = values[goals.FindIndex(g => g.Expression == "K(SP1.S)")].AsCube().RealValues;
        var byHand = values[goals.FindIndex(g => g.Expression == "K")].AsCube().RealValues;
        Assert.Equal(byHand.Length, k.Length);
        for (int f = 0; f < k.Length; f++) Assert.Equal(byHand[f], k[f], 1e-9);
    }

    /// <summary>A single point added after a sweep lies inside it, and must not narrow the goal's range to that point.</summary>
    [Fact]
    public void AGoalsRange_IsTheLowestAndHighestOfEverySegment()
    {
        const string cnl = """
            Port:P1 in  0   Num=1 Z=50 Ohm
            R:R1    in  out R=10 Ohm
            Port:P2 out 0   Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=10 npts=10 Unit=GHz
            analysis SP1 type=sparam start=2.45 stop=2.45 npts=1 Unit=GHz
            """;
        var range = GoalTemplates.For(PreparedCircuit.FromText(cnl, null, null)).First(t => t.Id == "SP1.sij").Make().Range!;
        Assert.Equal(("1 GHz", "10 GHz"), (range.Lo, range.Hi));
    }

    [Fact]
    public void WsProbeTemplates_EvaluateOnAProbedCircuit()
    {
        const string cnl = """
            Port:P1      in  0   Num=1 Z=50 Ohm
            R:Rs         in  g   R=20 Ohm
            C:Cg         g   0   C=1 pF
            WSProbe:GATE g   d
            R:Rd         d   0   R=200 Ohm
            L:Ld         d   out L=2 nH
            Port:P2      out 0   Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=3 npts=5 Unit=GHz
            """;
        var circuit = PreparedCircuit.FromText(cnl, null, null);
        Assert.Null(circuit.ReadError);
        var wsp = GoalTemplates.For(circuit).Where(t => t.Group == GoalTemplateGroup.WsProbe).ToList();
        Assert.Equal(17, wsp.Count);
        Assert.All(wsp, t => Assert.Equal(["GATE"], t.Fields[0].Choices));

        Evaluate(circuit, [.. wsp.SelectMany(Variants)]);
    }

    [Fact]
    public void ACustomExpression_IsValidatedAsTyped()
    {
        Assert.Null(GoalTemplates.Validate("dB(SP1.S(2, 1))"));
        Assert.False(string.IsNullOrEmpty(GoalTemplates.Validate("dB(SP1.S(2, 1)")));
        Assert.Equal(GoalTemplateGroup.Custom, GoalTemplates.For(new TestBench("tb")).Single().Group);
    }

    /// <summary>The template's default, plus each choice of a scale or part field.</summary>
    private static IEnumerable<OptimizationGoal> Variants(GoalTemplate t)
    {
        yield return t.Make();
        foreach (var f in t.Fields.Where(f => f.Key is "scale" or "part"))
            foreach (var c in f.Choices.Where(c => c != f.Default))
                yield return t.Make(new Dictionary<string, string> { [f.Key] = c });
    }

    private static List<Value> Evaluate(PreparedCircuit circuit, IReadOnlyList<OptimizationGoal> goals)
    {
        var rr = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
        {
            Measurements = true,
            Expressions  = [.. goals.Select(g => g.Expression)],
        });
        Assert.Equal(RunStatus.Success, rr.Status);
        Assert.Equal(goals.Count, rr.Expressions.Count);
        var values = new List<Value>();
        for (int i = 0; i < goals.Count; i++)
        {
            var o = rr.Expressions[i];
            Assert.True(o.Error is null, $"{goals[i].Expression}: {o.Error}");
            Assert.NotNull(o.Value);
            values.Add(o.Value!.Value);
        }
        return values;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
