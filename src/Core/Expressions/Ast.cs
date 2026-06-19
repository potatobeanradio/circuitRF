namespace CircuitRF.Core.Expressions;

// ── AST node hierarchy (§5) ──────────────────────────────────────────────────
// Pure data; no evaluation logic here.

public abstract record Expr;

/// <summary>Real numeric literal.</summary>
public sealed record NumberExpr(double Value) : Expr;

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
/// Used for SnP/N-port config params (File, Type, InterpMode, ExtrapMode).
/// </summary>
public sealed record StringLiteralExpr(string Value) : Expr;

/// <summary>Kind of one token inside a cube index: ':' (whole), a pin (int/label), or 'a:b' (range).</summary>
public enum IndexTokenKind { Whole, Pin, Range }

/// <summary>One positional token of a cube index. Pin uses A; Range uses A (start) and B (end-exclusive).</summary>
public sealed record IndexToken(IndexTokenKind Kind, Expr? A = null, Expr? B = null);

/// <summary>Positional cube index: Target[token, token, …]. Mirrors the trace-card slice shorthand.</summary>
public sealed record IndexExpr(Expr Target, IndexToken[] Tokens) : Expr;
