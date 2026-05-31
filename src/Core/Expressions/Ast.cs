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
