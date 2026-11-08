namespace CircuitRF.Core.Expressions;

// ── AST node hierarchy (§5) ──────────────────────────────────────────────────
// Pure data; no evaluation logic here.

public abstract record Expr;

/// <summary>
/// Real numeric literal. <paramref name="Unit"/> is set for a unit LITERAL (<c>10um</c>) and is its engine spelling;
/// <paramref name="Value"/> is then ALREADY in base SI, so every consumer that only needs the number reads it
/// unchanged. A literal with a unit is unit-bearing (<see cref="Evaluator.IsUnitBearing(Expr, Scope)"/>).
/// </summary>
public sealed record NumberExpr(double Value, string? Unit = null) : Expr;

/// <summary>Reserved constant: j, pi, e.</summary>
public sealed record ConstExpr(string Name) : Expr;

/// <summary>Variable, parameter, or function-argument reference (resolved against scope).</summary>
public sealed record RefExpr(string Name) : Expr;

/// <summary>Unary operator: -, +, !</summary>
public sealed record UnaryExpr(string Op, Expr Operand) : Expr;

/// <summary>Binary arithmetic: + - * / ^</summary>
public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>Comparison: &lt; &lt;= > >= == !=</summary>
public sealed record CompareExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>Logical: &amp;&amp; ||</summary>
public sealed record LogicExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>if(cond,then,else) or cond ? then : else. Short-circuits.</summary>
public sealed record ConditionalExpr(Expr Condition, Expr Then, Expr Else) : Expr;

/// <summary>Built-in or user-defined function call.</summary>
public sealed record CallExpr(string Name, Expr[] Args) : Expr;

/// <summary>
/// String literal: "foo". Storage-only — no string operators or coercions allowed.
/// Used for SnP/N-port config params (File, Type, InterpMode, InterpDomain, ExtrapMode).
/// </summary>
public sealed record StringLiteralExpr(string Value) : Expr;

/// <summary>
/// A port range <c>lo:hi</c> written as an argument of a port matrix, <c>SP1.S(1:2, 1:2)</c>: the ports numbered
/// <c>lo</c> to <c>hi</c>, both included, renumbered from 1 in the result. Only a port-matrix argument may hold one.
/// </summary>
public sealed record PortRangeExpr(Expr Lo, Expr Hi) : Expr;

/// <summary>Kind of one token inside a cube index: ':' (whole), a pin (int/label), or 'a:b' (range).</summary>
public enum IndexTokenKind { Whole, Pin, Range }

/// <summary>One positional token of a cube index. Pin uses A; Range uses A (start) and B (end-exclusive).</summary>
public sealed record IndexToken(IndexTokenKind Kind, Expr? A = null, Expr? B = null);

/// <summary>Positional cube index: Target[token, token, …]. Mirrors the trace-card slice shorthand.</summary>
public sealed record IndexExpr(Expr Target, IndexToken[] Tokens) : Expr;
