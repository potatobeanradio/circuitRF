namespace CircuitRF.Core.Expressions;

/// <summary>Base for all expression-engine errors (§15). Always names the offending text.</summary>
public class ExpressionException(string message) : Exception(message)
{
    /// <summary>A coded refusal; its message is the diagnostic rendered, so every caller reading the message is unchanged.</summary>
    public ExpressionException(CircuitRF.Diagnostics.Diagnostic diagnostic) : this(diagnostic.Render()) => Diagnostic = diagnostic;

    /// <summary>The coded refusal, when it was thrown as one.</summary>
    public CircuitRF.Diagnostics.Diagnostic? Diagnostic { get; }
}

public class CycleException(string chain)
    : ExpressionException($"Cyclic dependency detected: {chain}")
{
    public string Chain { get; } = chain;
}

public class UnresolvedNameException : ExpressionException
{
    public UnresolvedNameException(string name, string scope) : this(name, scope, null) { }

    /// <param name="why">What else is known about why the name is missing — appended to the message after
    /// a dash. Null for none.</param>
    public UnresolvedNameException(string name, string scope, string? why)
        : base($"Unresolved name '{name}' in scope '{scope}'" + (why is null ? "" : $" — {why}"))
    {
        Name  = name;
        Scope = scope;
    }

    public string Name  { get; }
    public string Scope { get; }
}

public class TypeErrorException(string message) : ExpressionException(message);

public class ArityException(string funcName, int expected, int actual)
    : ExpressionException($"Function '{funcName}' expects {expected} argument(s), got {actual}")
{
    public string FuncName { get; } = funcName;
}

public class UnknownFunctionException(string name)
    : ExpressionException($"Unknown function '{name}'")
{
    public string Name { get; } = name;
}

public class DomainException(string operation, string context)
    : ExpressionException($"Domain error in '{operation}': {context}")
{
    public string Operation { get; } = operation;
}

public class ParseException(string message, int position)
    : ExpressionException($"Parse error at position {position}: {message}")
{
    public int Position { get; } = position;
}
