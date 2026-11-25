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

/// <summary>A source's value as an expression operand: the number in base SI, or why there is none.</summary>
/// <param name="IsBool">An on/off value — a button — which enters the expression as a Bool, so <c>if(@button1, …)</c>,
/// <c>!@button1</c> and <c>&amp;&amp;</c> read it as the condition it is.</param>
public readonly record struct LinkOperandValue(double Value, string? Error = null, bool IsBool = false);

public static class LinkScope
{
    [ThreadStatic] private static Func<string, Axis, LinkMatchResult>? _current;
    [ThreadStatic] private static Func<string, LinkOperandValue>? _values;

    /// <summary>Makes <paramref name="resolver"/> the one <c>@name</c> slice tokens ask, and <paramref name="values"/> the
    /// one <c>@name</c> scalar operands ask (brief-ddl-6 R-ddl6-4), until the result is disposed.</summary>
    public static IDisposable Enter(Func<string, Axis, LinkMatchResult>? resolver,
                                    Func<string, LinkOperandValue>? values = null)
    {
        var previous = (_current, _values);
        _current = resolver;
        _values  = values;
        return new Restore(previous.Item1, previous.Item2);
    }

    /// <summary>The value of the source <paramref name="name"/> as a scalar operand, in base SI — a control's value, a
    /// marker's X, a button's 0 or 1.</summary>
    internal static bool TryValue(string name, out LinkOperandValue value, out string error)
    {
        value = default; error = "";
        if (_values is null)
        {
            error = $"'@{name}' is a display value, and only a Data Display has one to read.";
            return false;
        }
        var v = _values(name);
        if (v.Error is { } e) { error = e; return false; }
        value = v;
        return true;
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

    private sealed class Restore(Func<string, Axis, LinkMatchResult>? previous, Func<string, LinkOperandValue>? values)
        : IDisposable
    {
        public void Dispose() { _current = previous; _values = values; }
    }
}
