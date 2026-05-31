using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Expressions;

public class ParserTests
{
    private static Expr P(string s) => Parser.Parse(s);

    // ── Atoms ──────────────────────────────────────────────────────────────

    [Fact] public void NumberLiteral()
        => Assert.IsType<NumberExpr>(P("42"));

    [Fact] public void Ref()
        => Assert.IsType<RefExpr>(P("x"));

    [Fact] public void ConstJ()
        => Assert.IsType<ConstExpr>(P("j"));

    [Fact] public void ConstPi()
        => Assert.IsType<ConstExpr>(P("pi"));

    // ── Associativity & precedence ──────────────────────────────────────────

    [Fact] public void LeftAssocAdd()
    {
        // 1+2+3 → (1+2)+3 (left), so root is BinaryExpr(+, BinaryExpr(+,1,2), 3)
        var e = (BinaryExpr)P("1+2+3");
        Assert.Equal("+", e.Op);
        Assert.IsType<BinaryExpr>(e.Left);
        Assert.IsType<NumberExpr>(e.Right);
    }

    [Fact] public void MulBindsTighterThanAdd()
    {
        // 1+2*3 → 1+(2*3), root is +
        var e = (BinaryExpr)P("1+2*3");
        Assert.Equal("+", e.Op);
        var right = (BinaryExpr)e.Right;
        Assert.Equal("*", right.Op);
    }

    [Fact] public void CaretRightAssociative()
    {
        // 2^3^2 → 2^(3^2): root is ^ with left=2, right=^(3,2)
        var e = (BinaryExpr)P("2^3^2");
        Assert.Equal("^", e.Op);
        Assert.IsType<NumberExpr>(e.Left);
        var right = (BinaryExpr)e.Right;
        Assert.Equal("^", right.Op);
    }

    [Fact] public void UnaryMinusLowerThanCaret()
    {
        // -2^2 must parse as -(2^2), not (-2)^2
        // root should be UnaryExpr("-", BinaryExpr("^", 2, 2))
        var e = (UnaryExpr)P("-2^2");
        Assert.Equal("-", e.Op);
        var inner = (BinaryExpr)e.Operand;
        Assert.Equal("^", inner.Op);
    }

    [Fact] public void ParenOverridesPrecedence()
    {
        // (1+2)*3 → root is *
        var e = (BinaryExpr)P("(1+2)*3");
        Assert.Equal("*", e.Op);
    }

    [Fact] public void TernaryRightAssoc()
    {
        // a ? b : c ? d : e  →  a ? b : (c ? d : e)
        var e = (ConditionalExpr)P("a ? b : c ? d : e");
        Assert.IsType<RefExpr>(e.Then);
        Assert.IsType<ConditionalExpr>(e.Else);
    }

    [Fact] public void IfFunctionThreeArgs()
    {
        var e = (ConditionalExpr)P("if(x>0, 1, -1)");
        Assert.IsType<CompareExpr>(e.Condition);
        Assert.IsType<NumberExpr>(e.Then);
        Assert.IsType<UnaryExpr>(e.Else);
    }

    [Fact] public void FunctionCall()
    {
        var e = (CallExpr)P("sin(x)");
        Assert.Equal("sin", e.Name);
        Assert.Single(e.Args);
    }

    [Fact] public void FunctionCallMultiArg()
    {
        var e = (CallExpr)P("atan2(y, x)");
        Assert.Equal("atan2", e.Name);
        Assert.Equal(2, e.Args.Length);
    }

    [Fact] public void ComparisonLowerThanArithmetic()
    {
        // 1+2 > 2 → (1+2) > 2; root is CompareExpr
        var e = (CompareExpr)P("1+2 > 2");
        Assert.Equal(">", e.Op);
        Assert.IsType<BinaryExpr>(e.Left);
    }

    [Fact] public void LogicalOrLowest()
    {
        // a&&b || c — root is ||
        var e = (LogicExpr)P("a&&b || c");
        Assert.Equal("||", e.Op);
        Assert.IsType<LogicExpr>(e.Left);
    }
}
