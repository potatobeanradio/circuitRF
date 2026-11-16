// ================================================================
//  TimeVariable.cs  —  `time`, the simulated time (docs/design/transient.md §6.3;
//  brief-trn-2 R-trn2-7)
//
//  One rule, here, for every caller:
//
//  * In a transient analysis `time` is the simulated time. Whatever evaluates an
//    expression during the run binds it — a V_Expr/I_Expr source (WaveformSourceModel)
//    and an SDD's equations (SddModel.Time) — so the binding is the caller's, the
//    rule is not.
//  * Everywhere else the circuit is in steady state, where every finite instant
//    has passed: `time` as an operand of a comparison reads as +infinity, so
//    `time > 0` is true, `time < 1n` is false and `exp(-time/tau) < 0.5` is true
//    (spice-models.md §8.10). Anywhere else it has no steady-state value and is
//    refused, naming it.
//
//  The Evaluator applies the rule lazily (an unbound `time` resolves to +infinity
//  inside a comparison and throws outside one); SteadyStateError answers the same
//  question for an AST a compiled evaluator will run (the SDD's register program).
// ================================================================

namespace CircuitRF.Core.Expressions;

public static class TimeVariable
{
    /// <summary>The identifier.</summary>
    public const string Name = "time";

    /// <summary>The value an unbound <c>time</c> takes as an operand of a comparison: every finite instant has passed.</summary>
    public const double SteadyState = double.PositiveInfinity;

    /// <summary>The sentence for a <c>time</c> outside a condition where nothing binds it.</summary>
    public static string OutsideConditionMessage(string? where = null) =>
        (where is null ? "" : where + ": ")
        + "'time' is the simulated time, bound only in a transient analysis (type=tran); outside a condition "
        + "(time > t0, if(time < t1, ...)) it has no steady-state value, so this expression cannot be read by "
        + "a DC, S-parameter, harmonic-balance, loadpull or envelope analysis.";

    /// <summary>Whether <paramref name="ast"/> names <c>time</c> anywhere.</summary>
    public static bool Mentions(Expr ast) => AstWalker.CollectRefs(ast).Contains(Name);

    /// <summary>
    /// Null when <paramref name="ast"/> has a steady-state reading — it names <c>time</c> only as part of a comparison's
    /// operand, or not at all — and the refusal otherwise.
    /// </summary>
    public static string? SteadyStateError(Expr ast, string? where = null) =>
        Outside(ast, inCompare: false) ? OutsideConditionMessage(where) : null;

    private static bool Outside(Expr e, bool inCompare) => e switch
    {
        RefExpr r => !inCompare && r.Name == Name,
        UnaryExpr u => Outside(u.Operand, inCompare),
        BinaryExpr b => Outside(b.Left, inCompare) || Outside(b.Right, inCompare),
        CompareExpr c => Outside(c.Left, true) || Outside(c.Right, true),
        LogicExpr l => Outside(l.Left, inCompare) || Outside(l.Right, inCompare),
        ConditionalExpr cd => Outside(cd.Condition, inCompare) || Outside(cd.Then, inCompare) || Outside(cd.Else, inCompare),
        CallExpr cl => cl.Args.Any(a => Outside(a, inCompare)),
        IndexExpr ix => Outside(ix.Target, inCompare)
                        || ix.Tokens.Any(t => (t.A is { } a && Outside(a, inCompare)) || (t.B is { } b2 && Outside(b2, inCompare))),
        PortRangeExpr pr => Outside(pr.Lo, inCompare) || Outside(pr.Hi, inCompare),
        _ => false,
    };
}

/// <summary>A <c>time</c> read outside a condition where no transient analysis binds it.</summary>
public sealed class TimeOutsideConditionException(string? where = null)
    : ExpressionException(TimeVariable.OutsideConditionMessage(where));
