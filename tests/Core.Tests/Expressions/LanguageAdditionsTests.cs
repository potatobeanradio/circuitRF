using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Three v1 language capabilities that were specified but not reachable from a netlist, plus the
/// rounding family. Each is exercised through the full .cnl → elaborate path, not just the
/// evaluator, because the gap in every case was the wiring rather than the arithmetic.
/// </summary>
public class LanguageAdditionsTests
{
    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double ResistorValue(ElaboratedNetlist nl, string path)
        => nl.Components.Single(c => c.InstancePath == path).Parameters["R"].AsReal();

    // ── User-defined expression functions ─────────────────────────────────────

    [Fact]
    public void UserFunctionDeclaredInNetlist_IsCallableFromAComponentValue()
    {
        var nl = Elaborate(@"
area(w, h) = w*h
R:R1  a 0  R=area(3,4) Ohm
");
        Assert.Equal(12.0, ResistorValue(nl, "R1"), 10);
    }

    [Fact]
    public void UserFunctions_Compose_AndSeeGlobals()
    {
        var nl = Elaborate(@"
scale = 10
double(x) = 2*x
scaled(x) = double(x)*scale
R:R1  a 0  R=scaled(5) Ohm
");
        Assert.Equal(100.0, ResistorValue(nl, "R1"), 10);
    }

    [Fact]
    public void UserFunction_IsCallableFromACellParameterDefault()
    {
        // The registration must happen before flattening: a cell parameter default is evaluated
        // during elaboration, and may call one of these.
        var nl = Elaborate(@"
sq(x) = x*x
define Cell ( p )
  parameters Rv=sq(6)
  R:Rin  p 0  R=Rv Ohm
end Cell
Cell:X1  n1
");
        Assert.Equal(36.0, ResistorValue(nl, "X1.Rin"), 10);
    }

    [Fact]
    public void FunctionDeclarationInsideADefine_IsRejected()
    {
        var ex = Assert.Throws<CnlReadException>(() => Elaborate(@"
define Cell ( p )
  f(x) = x
  R:Rin  p 0  R=1 Ohm
end Cell
Cell:X1  n1
"));
        Assert.Contains("top level", ex.Message);
    }

    [Fact]
    public void ParenthesisedAssignmentThatIsNotADeclaration_StaysAVariable()
    {
        // `y = (a+b)*2` has parentheses but no parameter list before '='; it must remain a variable.
        var nl = Elaborate(@"
a = 1
b = 2
y = (a+b)*2
R:R1  n 0  R=y Ohm
");
        Assert.Equal(6.0, ResistorValue(nl, "R1"), 10);
    }

    // ── String equality ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"m1\"", 10.0)]
    [InlineData("\"m2\"", 20.0)]
    [InlineData("\"other\"", 30.0)]
    public void StringEquality_SelectsABranch(string group, double expected)
    {
        var nl = Elaborate($@"
grp = {group}
R:R1  n 0  R=if(grp==""m1"",10,if(grp==""m2"",20,30)) Ohm
");
        Assert.Equal(expected, ResistorValue(nl, "R1"), 10);
    }

    [Fact]
    public void StringInequality_Works()
    {
        var nl = Elaborate(@"
grp = ""a""
R:R1  n 0  R=if(grp!=""b"",7,9) Ohm
");
        Assert.Equal(7.0, ResistorValue(nl, "R1"), 10);
    }

    [Fact]
    public void StringComparedToANumber_IsStillAnError()
    {
        // Equality is the ONE operation String supports; it must not become a coercion loophole.
        var ex = Record.Exception(() => Elaborate(@"
grp = ""a""
R:R1  n 0  R=if(grp==1,7,9) Ohm
"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void StringInArithmetic_IsStillAnError()
    {
        var ex = Record.Exception(() => Elaborate(@"
grp = ""a""
R:R1  n 0  R=grp*2 Ohm
"));
        Assert.NotNull(ex);
    }

    // ── Rounding family ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("floor(2.7)",   2.0)]
    [InlineData("floor(-2.1)", -3.0)]
    [InlineData("ceil(2.1)",    3.0)]
    [InlineData("ceil(-2.7)",  -2.0)]
    [InlineData("round(2.5)",   3.0)]      // away from zero
    [InlineData("round(-2.5)", -3.0)]
    [InlineData("int(2.9)",     2.0)]      // truncate toward zero
    [InlineData("int(-2.9)",   -2.0)]
    public void RoundingFunctions_MatchTheirDefinitions(string expr, double expected)
    {
        var nl = Elaborate($"R:R1  n 0  R={expr} Ohm\n");
        Assert.Equal(expected, ResistorValue(nl, "R1"), 10);
    }

    [Fact]
    public void IntDiffersFromFloorOnNegatives()
    {
        // The distinction that matters: truncation toward zero vs downward rounding.
        var nl = Elaborate("R:R1  n 0  R=int(-3.5)-floor(-3.5) Ohm\n");
        Assert.Equal(1.0, ResistorValue(nl, "R1"), 10);
    }
}
