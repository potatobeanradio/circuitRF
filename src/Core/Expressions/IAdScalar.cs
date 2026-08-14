namespace CircuitRF.Core.Expressions;

/// <summary>
/// Real-only scalar interface for the generic SDD evaluator.
/// Implemented by SddScalar (plain double) and Dual (forward-mode AD).
/// One code path, two type arguments — value and derivative cannot diverge.
/// </summary>
public interface IAdScalar<T> where T : IAdScalar<T>
{
    // §1.3 step 2 (brief-harmonicarf-r3b) — operands taken by `in` (readonly reference) rather than
    // by value: T (Dual) is a ~144-byte struct carrying a fixed 16-wide gradient regardless of the
    // actual port+control count, so a by-value binary op previously copied ~288 bytes of input on
    // every Add/Sub/Mul/Div/Pow — pure waste for the (overwhelmingly common) case of N ≪ 16. The
    // RESULT is still returned by value (T is a functional, expression-tree-shaped API — there is no
    // caller-owned slot to write into without restructuring the whole evaluator around Span<double>
    // buffers, which is a bigger change than this step's scope), so this halves rather than
    // eliminates the copy volume. Every implementation and call site updated together; behaviour is
    // unchanged — same values, same operations, same order — only how they are passed differs.
    static abstract T Add(in T a, in T b);
    static abstract T Sub(in T a, in T b);
    static abstract T Mul(in T a, in T b);
    static abstract T Div(in T a, in T b);
    static abstract T Neg(in T a);
    static abstract T Pow(in T a, in T b);

    // Function table (§2.3). Exp is overflow-safe; Log/Sqrt clamp-and-warn on domain errors.
    static abstract T Exp(in T a);
    static abstract T Log(in T a);
    static abstract T Sqrt(in T a);
    static abstract T Tanh(in T a);
    static abstract T Sin(in T a);
    static abstract T Cos(in T a);
    static abstract T Abs(in T a);

    /// <summary>Lift a resolved constant or parameter value (zero gradient).</summary>
    static abstract T Constant(double d);

    /// <summary>Extract the scalar value for condition evaluation in conditionals.</summary>
    static abstract double ValueOf(in T a);
}
