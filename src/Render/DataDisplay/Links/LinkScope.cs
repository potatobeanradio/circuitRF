// ================================================================
//  LinkScope.cs  —  how an expression operand reaches its link
//
//  A multi-cube expression (`Pout[@m1, :] - Pin[@m1, :]`) is sliced deep
//  inside TraceExpression, which knows a DataSet and nothing about a
//  display. TraceResolve opens a scope around the one trace it is
//  resolving, holding that trace's own resolver (Trace.ExpressionLinks,
//  installed by LinkResolve); the `@name` token asks it. Outside a scope
//  — an expression evaluated with no display behind it — the token is an
//  error that says so, never a silent index 0.
// ================================================================

using System;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class LinkScope
{
    [ThreadStatic] private static Func<string, Axis, LinkMatchResult>? _current;

    /// <summary>Makes <paramref name="resolver"/> the one <c>@name</c> operands ask, until the result is disposed.</summary>
    public static IDisposable Enter(Func<string, Axis, LinkMatchResult>? resolver)
    {
        var previous = _current;
        _current = resolver;
        return new Restore(previous);
    }

    internal static bool TryIndex(string name, Axis axis, out int index, out string error)
    {
        index = 0; error = "";
        if (_current is null)
        {
            error = $"'@{name}' follows a marker, and only a Data Display has markers to follow.";
            return false;
        }
        index = _current(name, axis).Index;
        return true;
    }

    private sealed class Restore(Func<string, Axis, LinkMatchResult>? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }
}
