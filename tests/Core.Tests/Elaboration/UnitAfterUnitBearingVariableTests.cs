using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// brief-sdl-0 R-sdl0-3e — a parameter written <c>W = w50 mm</c> where <c>w50 = 280.152 um</c> (the spelling the
/// schematic-driven layout set's C2 keeps on two of its lines). The site unit is skipped because the expression
/// references a unit-bearing variable (var-unit-wins, docs/design/expressions.md §8), so it is the same width as
/// <c>W = w50</c> — not 280.152 mm.
/// </summary>
public class UnitAfterUnitBearingVariableTests
{
    [Fact]
    public void A_unit_after_a_unit_bearing_variable_is_skipped()
    {
        var tb = new TestBench("tb");
        tb.GlobalVariables.Add(new Variable("w50", "280.152", "um"));
        tb.Instances.Add(new Instance("R1", "R", ["a", "0"], [new ParameterAssignment("R", "w50", "mm")]));
        tb.Instances.Add(new Instance("R2", "R", ["b", "0"], [new ParameterAssignment("R", "w50")]));

        var nl = new Elaborator(new Library("test")).Elaborate(tb);

        double withUnit = nl.Components.Single(c => c.InstancePath == "R1").Parameters["R"].AsReal();
        double without  = nl.Components.Single(c => c.InstancePath == "R2").Parameters["R"].AsReal();
        Assert.Equal(280.152e-6, withUnit, 1e-15);
        Assert.Equal(without, withUnit);
    }
}
