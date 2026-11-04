using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// brief-em3d-51 R-em3d51-0 — what the circuit side does when a cell has a parameter AND a VAR of the same name.
/// <c>Elaborator.BuildCellScope</c> binds the parameter defaults, then the cell's variables, then the instance overrides,
/// each replacing the last; a comment in <c>SubcircuitTranslation.CarryGlobals</c> reads as if the variable would seal the
/// name shut against an override. This pins which is true. Nothing on the circuit side changes because of it.
/// </summary>
public class SameNameVarAndParameterTests
{
    [Fact]
    public void Var_replaces_the_default()
    {
        var cell = new Cell("Stub");
        cell.Ports.AddRange(["P1", "P2"]);
        cell.Parameters.Add(new ParameterDeclaration("w", "10"));
        cell.Variables.Add(new Variable("w", "12"));
        cell.Instances.Add(new Instance("C1", "C", ["P1", "P2"], [new ParameterAssignment("C", "w")]));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "Stub", ["a", "0"]));

        var lib = new Library("test");
        lib.Cells.Add(cell);
        var nl = new Elaborator(lib).Elaborate(tb);

        // No override: the VAR's 12, not the parameter's default of 10 — the cell has two defaults for one name.
        Assert.Equal(12.0, nl.Components.Single(c => c.InstancePath == "X1.C1").Parameters["C"].AsReal(), 1e-12);
    }

    /// <summary>brief-sdl-6 D7: an override of such a name is ambiguous — it could mean the parameter or the variable —
    /// so it is an error naming both, rather than silently reaching the name through the variable.</summary>
    [Fact]
    public void An_override_of_a_name_that_is_both_is_refused_naming_both()
    {
        var cell = new Cell("Stub");
        cell.Ports.AddRange(["P1", "P2"]);
        cell.Parameters.Add(new ParameterDeclaration("w", "10"));
        cell.Variables.Add(new Variable("w", "12"));
        cell.Instances.Add(new Instance("C1", "C", ["P1", "P2"], [new ParameterAssignment("C", "w")]));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X2", "Stub", ["b", "0"], [new ParameterAssignment("w", "20")]));
        var lib = new Library("test");
        lib.Cells.Add(cell);

        var ex = Assert.Throws<InvalidOperationException>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("both as a parameter and as a variable", ex.Message);
    }
}
