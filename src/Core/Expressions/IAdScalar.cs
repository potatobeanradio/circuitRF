namespace CircuitRF.Core.Expressions;

/// <summary>
/// Real-only scalar interface for the generic SDD evaluator.
/// Implemented by SddScalar (plain double) and Dual (forward-mode AD).
/// One code path, two type arguments — value and derivative cannot diverge.
/// </summary>
public interface IAdScalar<T> where T : IAdScalar<T>
{
    static abstract T Add(T a, T b);
    static abstract T Sub(T a, T b);
    static abstract T Mul(T a, T b);
    static abstract T Div(T a, T b);
    static abstract T Neg(T a);
    static abstract T Pow(T a, T b);

    // Function table (§2.3). Exp is overflow-safe; Log/Sqrt clamp-and-warn on domain errors.
    static abstract T Exp(T a);
    static abstract T Log(T a);
    static abstract T Sqrt(T a);
    static abstract T Tanh(T a);
    static abstract T Sin(T a);
    static abstract T Cos(T a);
    static abstract T Abs(T a);

    /// <summary>Lift a resolved constant or parameter value (zero gradient).</summary>
    static abstract T Constant(double d);

    /// <summary>Extract the scalar value for condition evaluation in conditionals.</summary>
    static abstract double ValueOf(T a);
}
