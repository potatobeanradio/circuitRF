using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Expressions;

public class CycleDetectionTests
{
    // ── Valid multi-hop chain (recursion.cnl fixture) ─────────────────────────

    [Fact]
    public void ValidChainResolvesToTwo()
    {
        // C2 = gizmo, gizmo = funtimes, funtimes = 2
        // Resolving C2 must walk the chain and return 2, not hang or error.
        var scope = new Scope("global");
        scope.Bind("C2",      "gizmo");
        scope.Bind("gizmo",   "funtimes");
        scope.Bind("funtimes","2");

        var v = new Evaluator().Resolve("C2", scope);
        Assert.Equal(ValueKind.Real, v.Kind);
        Assert.Equal(2.0, v.AsReal());
    }

    [Fact]
    public void ValidChainMemoized()
    {
        // Resolving through the chain twice should use memoization.
        var scope = new Scope("global");
        scope.Bind("a", "b");
        scope.Bind("b", "5");

        var ev = new Evaluator();
        var v1 = ev.Resolve("a", scope);
        var v2 = ev.Resolve("a", scope); // from memo
        Assert.Equal(5.0, v1.AsReal());
        Assert.Equal(5.0, v2.AsReal());
    }

    // ── Cyclic fixture ────────────────────────────────────────────────────────

    [Fact]
    public void DirectCycleIsReported()
    {
        // a = b, b = a — must throw CycleException, never hang
        var scope = new Scope("global");
        scope.Bind("a", "b");
        scope.Bind("b", "a");

        var ex = Assert.Throws<CycleException>(() => new Evaluator().Resolve("a", scope));
        // chain must name both variables
        Assert.Contains("a", ex.Chain);
        Assert.Contains("b", ex.Chain);
    }

    [Fact]
    public void SelfReferenceCycleIsReported()
    {
        var scope = new Scope("global");
        scope.Bind("x", "x + 1");

        Assert.Throws<CycleException>(() => new Evaluator().Resolve("x", scope));
    }

    [Fact]
    public void LongChainCycleReported()
    {
        var scope = new Scope("global");
        scope.Bind("a", "b");
        scope.Bind("b", "c");
        scope.Bind("c", "a"); // cycle at depth 3

        var ex = Assert.Throws<CycleException>(() => new Evaluator().Resolve("a", scope));
        Assert.Contains("a", ex.Chain);
        Assert.Contains("c", ex.Chain);
    }

    [Fact]
    public void UserFunctionRecursionDetected()
    {
        // A user function that directly recurses into itself
        var ev = new Evaluator();
        ev.RegisterFunction(new UserFunction("f", ["x"], "f(x)"));
        Assert.Throws<CycleException>(() => ev.Eval("f(1)", new Scope("test")));
    }

    [Fact]
    public void MutualRecursionDetected()
    {
        // f calls g calls f
        var ev = new Evaluator();
        ev.RegisterFunction(new UserFunction("f", ["x"], "g(x)"));
        ev.RegisterFunction(new UserFunction("g", ["x"], "f(x)"));
        Assert.Throws<CycleException>(() => ev.Eval("f(1)", new Scope("test")));
    }
}
