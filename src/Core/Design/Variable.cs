namespace CircuitRF.Core.Design;

/// <summary>
/// A named expression binding: global (on a TestBench) or cell-scoped.
/// Scope is structural — not string-keyed (§9).
/// </summary>
public sealed class Variable(string name, string expression, string? unit = null)
{
    public string  Name       { get; } = name;
    public string  Expression { get; } = expression;
    public string? Unit       { get; } = unit;
}
