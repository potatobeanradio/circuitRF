// Owner: PCD-3 (brief-pcd-3 R-pcd3-3).

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd3StretchTests
{
    /// <summary>R-pcd3-3: Stretch leaves the shape where it is at the current value, moves the moving vertices by δ along
    /// the axis when the parameter grows by δ, appends the binding, and refuses an anchor that also moves.</summary>
    [Fact]
    public void StretchMovesTheMovingVerticesByTheParametersGrowth()
    {
        var shape = new EditableComponent { Symbol = SymbolKind.MShape, InstanceName = "SH1" };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
            shape.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit });
        var ev = new Evaluator();
        // A scope per value: the evaluator memoises a binding by its scope's name.
        Scope At(string lpad) { var s = new Scope("cell" + lpad); s.Bind("Lpad", lpad, "um"); return s; }
        double V(EditableComponent c, string name, Scope s)
        {
            var row = c.Parameters.Single(q => q.Name == name);
            return ev.Eval(row.Expression, s, UnitNormalizer.ToEngineUnit(row.Unit)).AsReal();
        }

        var binding = new StretchBinding { Parameter = "Lpad", Anchor = 1, Moving = [2, 3], AxisDeg = 0 };
        var stretched = DrawnMetal.Stretch(shape, binding, At("200"), ev);

        Assert.Equal(100e-6, V(stretched, "X[2]", At("200")), 15);
        Assert.Equal(150e-6, V(stretched, "X[2]", At("250")), 15);
        Assert.Equal(150e-6, V(stretched, "X[3]", At("250")), 15);
        Assert.Equal(0.0, V(stretched, "X[1]", At("250")), 15);
        Assert.Equal(-50e-6, V(stretched, "Y[2]", At("250")), 15);
        Assert.Equal(new[] { binding }, stretched.Stretch);
        Assert.Equal("100", shape.Parameters.Single(q => q.Name == "X[2]").Expression);

        var up = DrawnMetal.Stretch(stretched, binding with { Moving = [3, 4], AxisDeg = 90 }, At("200"), ev);
        Assert.Equal(80e-6, V(up, "Y[4]", At("230")), 15);
        Assert.Equal(2, up.Stretch!.Count);

        Assert.Throws<ArgumentException>(() => DrawnMetal.Stretch(shape, binding with { Moving = [1, 2] }, At("200"), ev));
    }
}
