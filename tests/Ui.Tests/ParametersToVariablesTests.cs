using System.Runtime.CompilerServices;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Commands.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>Parameters to Variables on the PCell Designer example bench: a designed cell (X1) and an MLIN (TL1).</summary>
public sealed class ParametersToVariablesTests
{
    private static string Bench([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "examples", "PCell Designer", "Bench", "schematic", "Bench.csch"));

    /// <summary>Every instance parameter that evaluates to a number, in base SI, as the netlist reads it.</summary>
    private static Dictionary<string, double> Evaluated(SchematicEditModel model)
    {
        var tb = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance).TestBench;
        var scope = new Scope("global");
        foreach (var v in tb.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();
        var values = new Dictionary<string, double>();
        foreach (var inst in tb.Instances)
            foreach (var ov in inst.Overrides)
                try { if (evaluator.Eval(ov.Expression, scope, ov.Unit) is { Kind: ValueKind.Real } v) values[$"{inst.InstanceName}.{ov.Name}"] = v.AsReal(); }
                catch (Exception) { /* not a number */ }
        return values;
    }

    [Fact]
    public void CellAndLine_BecomeOneVarBlock_AndTheNetlistKeepsItsNumbers()
    {
        string path = Bench();
        var model = SchematicPersistence.LoadFromFile(path).model;
        model.SchematicDirectory = Path.GetDirectoryName(path);
        var before = Evaluated(model);
        var x1 = model.Components.Single(c => c.InstanceName == "X1");
        var tl1 = model.Components.Single(c => c.InstanceName == "TL1");
        int count = model.Components.Count;

        var cmd = ParametersToVariables.Command(model, [x1, tl1]);
        Assert.NotNull(cmd);
        cmd!.Execute();

        var block = Assert.Single(model.Components, c => c.Symbol == SymbolKind.Var);
        Assert.Equal(["X1_Lpad 200 um", "TL1_W 1.1 mm", "TL1_L 5 mm"],
                     block.Parameters.Select(p => $"{p.Name} {p.Expression} {p.Unit}"));
        Assert.Equal("X1_Lpad", x1.Parameters.Single(p => p.Name == "Lpad").Expression);
        Assert.Equal(["TL1_W", "TL1_L"], tl1.Parameters.Where(p => p.Name is "W" or "L").Select(p => p.Expression));

        var after = Evaluated(model);
        foreach (var (key, value) in before) Assert.Equal(value, after[key], 12);
        Assert.Equal(1.1e-3, after["TL1.W"], 12);

        // Already expressions now: a second run on the same parts has nothing to convert.
        Assert.Null(ParametersToVariables.Command(model, [x1, tl1]));

        cmd.Undo();
        Assert.Equal(count, model.Components.Count);
        Assert.Equal("200", x1.Parameters.Single(p => p.Name == "Lpad").Expression);
    }
}
