using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>Task 1 tests: SDD expressions may contain whitespace; multiple assignments per line.</summary>
public class SddWhitespaceTests
{
    private static SddModel ParseSdd(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (SddModel)nl.Components.First(c => c.Model is SddModel).Model;
    }

    // ── Whitespace in expressions ─────────────────────────────────────────────

    [Fact]
    public void SpacedExpression_ParsesIdenticallyToUnspaced()
    {
        var spaced = ParseSdd(@"
R_val = 50
SDD:S1  n1 0  I[1,0] = _v1 / R_val
");
        var unspaced = ParseSdd(@"
R_val = 50
SDD:S1  n1 0  I[1,0]=_v1/R_val
");
        // Both should return i = v/50, dg = 1/50
        var r1 = spaced.Evaluate(new PortVoltages([10.0]));
        var r2 = unspaced.Evaluate(new PortVoltages([10.0]));
        Assert.Equal(r2.I[0],    r1.I[0],    10);
        Assert.Equal(r2.Dg[0,0], r1.Dg[0,0], 10);
    }

    [Fact]
    public void HeavilySpacedExpression_EvaluatesCorrectly()
    {
        var sdd = ParseSdd(@"
SDD:S1  n1 0  I[1,0] = ( _v1 * 2.0 ) / 100.0
");
        var r = sdd.Evaluate(new PortVoltages([5.0]));
        Assert.Equal(5.0 * 2.0 / 100.0, r.I[0], 10);
        Assert.Equal(2.0 / 100.0,        r.Dg[0,0], 10);
    }

    // ── Multiple assignments per line ─────────────────────────────────────────

    [Fact]
    public void MultipleAssignments_OnOneLine_ParseCorrectly()
    {
        // Both I[1,0] and I[2,0] on the same line, with spaces in expressions.
        var sdd = ParseSdd(@"
R1 = 100
G2 = 0.01
SDD:X1  gate 0 drain 0  I[1,0] = _v1 / R1  I[2,0] = _v2 * G2
");
        var r = sdd.Evaluate(new PortVoltages([5.0, 20.0]));
        Assert.Equal(5.0 / 100.0,  r.I[0], 10);
        Assert.Equal(20.0 * 0.01,  r.I[1], 10);
        Assert.Equal(1.0 / 100.0,  r.Dg[0,0], 10);
        Assert.Equal(0.01,         r.Dg[1,1], 10);
    }

    [Fact]
    public void MultipleAssignments_ComplexExpressions_ParseCorrectly()
    {
        // I[2,0] with nested parentheses, then I[1,0] after it — reverse order should still work.
        var sdd = ParseSdd(@"
B = 2.0
SDD:X1  n1 0 n2 0  I[1,0] = _v1 / 50  I[2,0] = (B * _v2) / (1.0 + _v2 * 0.001)
");
        double v1 = -3.0, v2 = 10.0;
        var r = sdd.Evaluate(new PortVoltages([v1, v2]));
        Assert.Equal(v1 / 50.0, r.I[0], 10);
        double expectedI2 = (2.0 * v2) / (1.0 + v2 * 0.001);
        Assert.Equal(expectedI2, r.I[1], 8);
    }

    // ── No false split on 'I' inside an expression ────────────────────────────

    [Fact]
    public void VariableNamedIdss_NotMissplitAsAssignment()
    {
        // 'Idss' starts with 'I' but is NOT 'I[p,w]=' — must not be treated as a new assignment.
        var sdd = ParseSdd(@"
Idss = 1.5
SDD:X1  n1 0  I[1,0] = Idss * _v1
");
        var r = sdd.Evaluate(new PortVoltages([4.0]));
        Assert.Equal(1.5 * 4.0, r.I[0], 10);
        Assert.Equal(1.5,       r.Dg[0,0], 10);
    }

    [Fact]
    public void ExpressionContainingI_InParens_NotMissplit()
    {
        // Expression has '(I2 * _v1)' where I2 is a variable — the 'I2' must not be treated
        // as a new assignment (it's not followed by '[digits,digits]=').
        var sdd = ParseSdd(@"
I2 = 3.0
SDD:X1  n1 0  I[1,0] = I2 * _v1
");
        var r = sdd.Evaluate(new PortVoltages([2.0]));
        Assert.Equal(3.0 * 2.0, r.I[0], 10);
    }

    // ── Line continuation ─────────────────────────────────────────────────────

    [Fact]
    public void BackslashContinuation_JoinsLines()
    {
        var sdd = ParseSdd("R_v = 50\nSDD:S1  n1 0  I[1,0] = _v1 \\\n    / R_v\n");
        var r = sdd.Evaluate(new PortVoltages([10.0]));
        Assert.Equal(10.0 / 50.0, r.I[0], 10);
    }

    // ── Hero equation with spaces works ──────────────────────────────────────

    [Fact]
    public void HeroEquation_WithSpaces_MatchesSpaceless()
    {
        const string heroI2NoSpaces =
            "(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*log(exp(-(Sv-_v1)/Sc)+1)))+1))" +
            "*log(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*log(exp(-(Sv-_v1)/Sc)+1))/TC)+1)" +
            "*(_v2*lam+1))/2";

        // Space-free version (baseline — must still work)
        var sddNoSpaces = ParseSdd($@"
Sv=-0.837
Sc=0.71
TV0=4.268
TC=1.507
th=0.001
a=0.176
g=0.089
lam=0.0012
B=1130
SDD:M1  gate 0 drain 0  I[1,0]=_v1/50  I[2,0]={heroI2NoSpaces}
");
        var r0 = sddNoSpaces.Evaluate(new PortVoltages([-3.05, 48.0]));

        // Spaced version — same equation with spaces around operators
        const string heroI2Spaced =
            "(B * TC * tanh(_v2 * a * (tanh(g * (TV0 - _v1 + _v2 * th + Sc * log(exp(-(Sv - _v1) / Sc) + 1))) + 1))" +
            " * log(exp(-(2 * TV0 - 2 * _v1 + 2 * _v2 * th + 2 * Sc * log(exp(-(Sv - _v1) / Sc) + 1)) / TC) + 1)" +
            " * (_v2 * lam + 1)) / 2";

        var sddSpaced = ParseSdd($@"
Sv = -0.837
Sc = 0.71
TV0 = 4.268
TC = 1.507
th = 0.001
a = 0.176
g = 0.089
lam = 0.0012
B = 1130
SDD:M1  gate 0 drain 0  I[1,0] = _v1 / 50  I[2,0] = {heroI2Spaced}
");
        var r1 = sddSpaced.Evaluate(new PortVoltages([-3.05, 48.0]));

        // Values must match to 8 decimal places
        Assert.Equal(r0.I[0],    r1.I[0],    8);
        Assert.Equal(r0.I[1],    r1.I[1],    8);
        Assert.Equal(r0.Dg[1,0], r1.Dg[1,0], 6);
        Assert.Equal(r0.Dg[1,1], r1.Dg[1,1], 6);
    }

    // ── Assignment-boundary gaps found 2026-09-02 ─────────────────────────────
    //
    // SddAssignmentHeader is the scanner that decides where one assignment's expression ENDS. It
    // knew I[…], Q[…], F[…], H[…], C[…] and Cport[…] — and neither V[p] nor a plainly-named
    // constant, both of which the factory reads. The failure hid because a line whose ONLY
    // assignment is one of those never reaches this scanner: the generic whitespace-token path
    // handles it, so every existing test of V[p] passes. Put one beside an equation and the
    // PRECEDING equation swallows it, and the line dies pointing at a column of an expression
    // nobody wrote ("Parse error at position 12: Unexpected '='").

    [Fact]
    public void BranchEquation_IsAnAssignmentBoundary_NotPartOfThePreviousExpression()
    {
        var sdd = ParseSdd(@"
SDD:S1  n1 0  n2 0  I[1,0]=_v1/50  V[2]=0.5*_v1
");
        // Port 2's voltage is held, so it carries a branch row; port 1 states a current.
        // BranchPorts is 0-based (it indexes the per-port arrays), so V[2] reads as 1.
        Assert.Equal([1], sdd.BranchPorts);

        var r = sdd.Evaluate(new PortVoltages([10.0, 0.0]));
        Assert.Equal(10.0 / 50.0, r.I[0], 10);
    }

    [Fact]
    public void APlainlyNamedConstant_IsAnAssignmentBoundary_AndReachesTheEquations()
    {
        // Rscale is an ordinary per-instance value: resolved once at elaboration and bound in the
        // scope the equations evaluate in. Before the fix this text did not parse at all.
        var sdd = ParseSdd(@"
SDD:S1  n1 0  I[1,0]=_v1/Rscale  Rscale=25
");
        var r = sdd.Evaluate(new PortVoltages([10.0]));
        Assert.Equal(10.0 / 25.0, r.I[0], 10);
    }

    /// <summary>
    /// The guard on that new alternative: an unparenthesised <c>==</c> must not read as the start of
    /// a new assignment. Without <c>=(?!=)</c> the scanner takes "_v1 =" as a header and hands the
    /// rest, "= Vth", to the parser — so this line would not read at all.
    ///
    /// <para>Asserted as "the line parses", not as a value: a comparison is Bool and the SDD refuses
    /// a Bool as a scalar at EVALUATE time, which is a different (and correct) refusal.</para>
    /// </summary>
    [Fact]
    public void AnUnparenthesisedComparison_IsNotMistakenForAnAssignment()
    {
        var ex = Record.Exception(() => ParseSdd(@"
Vth = 1
SDD:S1  n1 0  I[1,0]=_v1 == Vth
"));
        Assert.Null(ex);
    }
}
