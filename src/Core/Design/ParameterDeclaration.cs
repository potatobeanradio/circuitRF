namespace CircuitRF.Core.Design;

/// <summary>
/// A parameter declared on a Cell — its name, default expression, and optional unit.
/// Resolved at elaboration time; the design layer holds only the symbolic form.
/// </summary>
public sealed class ParameterDeclaration(string name, string defaultExpression, string? unit = null, bool hidden = false)
{
    public string  Name              { get; } = name;
    public string  DefaultExpression { get; } = defaultExpression;
    public string? Unit              { get; } = unit;
    public bool    Hidden            { get; } = hidden;
}

/// <summary>
/// An override applied by an Instance — binds a parameter name to an expression
/// evaluated in the PARENT scope (§9 scope rules).
/// </summary>
public sealed class ParameterAssignment(string name, string expression, string? unit = null)
{
    public string  Name       { get; } = name;
    public string  Expression { get; } = expression;
    public string? Unit       { get; } = unit;
}
