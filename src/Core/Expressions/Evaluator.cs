using System.Numerics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Walks a scope chain, evaluates AST nodes, enforces cycle detection and memoization (§10).
/// One Evaluator instance per elaboration run; holds the resolving-stack and memo cache.
/// </summary>
public sealed class Evaluator
{
    // names currently being resolved — the cycle-detection stack
    private readonly List<string> _resolving = [];
    // fully resolved values, keyed by qualified name "scope::name"
    private readonly Dictionary<string, Value> _memo = new(StringComparer.Ordinal);
    // user-defined functions registered for this evaluator
    private readonly Dictionary<string, UserFunction> _functions = new(StringComparer.Ordinal);

    public void RegisterFunction(UserFunction fn) => _functions[fn.Name] = fn;

    /// <summary>
    /// Directly inject a pre-resolved value into the memo cache.
    /// Used by the Elaborator to store override values that were evaluated
    /// in the parent scope without re-parsing through ToString().
    /// Key format: "{scopeDebugName}::{name}"
    /// </summary>
    public void InjectResolved(string scopeDebugName, string name, Value value)
        => _memo[$"{scopeDebugName}::{name}"] = value;

    /// <summary>
    /// Resolve a named binding in the given scope, with cycle detection and memoization.
    /// </summary>
    public Value Resolve(string name, Scope scope)
    {
        var found = scope.Lookup(name)
            ?? throw new UnresolvedNameException(name, scope.DebugName);

        var (expression, unit, owningScope) = found;
        var key = $"{owningScope.DebugName}::{name}";

        if (_memo.TryGetValue(key, out var cached))
            return cached;

        // cycle guard
        int existingIdx = _resolving.IndexOf(name);
        if (existingIdx >= 0)
        {
            var chain = string.Join(" → ", _resolving[existingIdx..]) + " → " + name;
            throw new CycleException(chain);
        }

        _resolving.Add(name);
        try
        {
            var ast  = Parser.Parse(expression);
            var raw  = EvalExpr(ast, owningScope);
            var val  = ApplyUnit(raw, unit);
            _memo[key] = val;
            return val;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    /// <summary>
    /// Evaluate an already-parsed AST in the given scope.
    /// Used for expressions that aren't named (e.g. inline parameter values).
    /// </summary>
    public Value EvalExpr(Expr expr, Scope scope) => expr switch
    {
        StringLiteralExpr s => new Value(s.Value),
        NumberExpr      n  => new Value(n.Value),
        ConstExpr       c  => EvalConst(c.Name),
        RefExpr         r  => Resolve(r.Name, scope),
        UnaryExpr       u  => EvalUnary(u, scope),
        BinaryExpr      b  => EvalBinary(b, scope),
        CompareExpr     cmp => EvalCompare(cmp, scope),
        LogicExpr       lg => EvalLogic(lg, scope),
        ConditionalExpr cd => EvalConditional(cd, scope),
        CallExpr        cl => EvalCall(cl, scope),
        _ => throw new ExpressionException($"Unknown AST node: {expr.GetType().Name}")
    };

    /// <summary>Parse and evaluate an expression string in the given scope.</summary>
    public Value Eval(string expression, Scope scope, string? unit = null)
    {
        var ast = Parser.Parse(expression);
        var raw = EvalExpr(ast, scope);
        return ApplyUnit(raw, unit);
    }

    // ── Nodes ────────────────────────────────────────────────────────────────

    private static Value EvalConst(string name) => name switch
    {
        "j"  => Value.J,
        "pi" => Value.Pi,
        "e"  => Value.E,
        _    => throw new ExpressionException($"Unknown constant '{name}'")
    };

    private Value EvalUnary(UnaryExpr u, Scope scope)
    {
        var operand = EvalExpr(u.Operand, scope);
        return u.Op switch
        {
            "-" => Value.Negate(operand),
            "+" => Value.UnaryPlus(operand),
            "!" => Value.Not(operand),
            _   => throw new ExpressionException($"Unknown unary op '{u.Op}'")
        };
    }

    private Value EvalBinary(BinaryExpr b, Scope scope)
    {
        var l = EvalExpr(b.Left, scope);
        var r = EvalExpr(b.Right, scope);
        return b.Op switch
        {
            "+" => Value.Add(l, r),
            "-" => Value.Sub(l, r),
            "*" => Value.Mul(l, r),
            "/" => Value.Div(l, r),
            "^" => Value.Pow(l, r),
            _   => throw new ExpressionException($"Unknown binary op '{b.Op}'")
        };
    }

    private Value EvalCompare(CompareExpr cmp, Scope scope)
    {
        var l = EvalExpr(cmp.Left, scope);
        var r = EvalExpr(cmp.Right, scope);
        return cmp.Op switch
        {
            "<"  => Value.LessThan(l, r),
            "<=" => Value.LessOrEqual(l, r),
            ">"  => Value.GreaterThan(l, r),
            ">=" => Value.GreaterOrEqual(l, r),
            "==" => Value.Equal(l, r),
            "!=" => Value.NotEqual(l, r),
            _    => throw new ExpressionException($"Unknown comparison op '{cmp.Op}'")
        };
    }

    private Value EvalLogic(LogicExpr lg, Scope scope)
    {
        // short-circuit
        var l = EvalExpr(lg.Left, scope);
        if (l.Kind != ValueKind.Bool)
            throw new TypeErrorException($"Operator '{lg.Op}' requires Bool, got {l.Kind}");
        if (lg.Op == "&&" && !l.AsBool()) return Value.False;
        if (lg.Op == "||" && l.AsBool())  return Value.True;
        var r = EvalExpr(lg.Right, scope);
        if (r.Kind != ValueKind.Bool)
            throw new TypeErrorException($"Operator '{lg.Op}' requires Bool, got {r.Kind}");
        return Value.And(l, r); // works for both since we already short-circuited
    }

    private Value EvalConditional(ConditionalExpr cd, Scope scope)
    {
        var cond = EvalExpr(cd.Condition, scope);
        if (cond.Kind != ValueKind.Bool)
            throw new TypeErrorException($"Condition must be Bool, got {cond.Kind}");
        // short-circuit: evaluate only the selected branch
        return cond.AsBool()
            ? EvalExpr(cd.Then, scope)
            : EvalExpr(cd.Else, scope);
    }

    private Value EvalCall(CallExpr cl, Scope scope)
    {
        // user-defined function?
        if (_functions.TryGetValue(cl.Name, out var ufn))
            return CallUserFunction(ufn, cl.Args, scope);

        // built-ins
        return cl.Name switch
        {
            "sin"   => UnaryMath(cl, scope, Math.Sin,    Complex.Sin),
            "cos"   => UnaryMath(cl, scope, Math.Cos,    Complex.Cos),
            "tan"   => UnaryMath(cl, scope, Math.Tan,    Complex.Tan),
            "asin"  => UnaryMath(cl, scope, Math.Asin,   x => new Complex(Math.Asin(x.Real), 0)),
            "acos"  => UnaryMath(cl, scope, Math.Acos,   x => new Complex(Math.Acos(x.Real), 0)),
            "atan"  => UnaryMath(cl, scope, Math.Atan,   x => new Complex(Math.Atan(x.Real), 0)),
            "atan2" => BinaryRealMath(cl, scope, Math.Atan2),
            "sinh"  => UnaryMath(cl, scope, Math.Sinh,   Complex.Sinh),
            "cosh"  => UnaryMath(cl, scope, Math.Cosh,   Complex.Cosh),
            "tanh"  => UnaryMath(cl, scope, Math.Tanh,   Complex.Tanh),
            "exp"   => UnaryMath(cl, scope, Math.Exp,    Complex.Exp),
            "log"   => UnaryMath(cl, scope, SafeLog,     Complex.Log),
            "log10" => UnaryMath(cl, scope, SafeLog10,   x => Complex.Log(x, 10)),
            "sqrt"  => UnaryMath(cl, scope, SafeSqrt,    Complex.Sqrt),
            "pow"   => BinaryMath(cl, scope, Math.Pow,   Complex.Pow),
            "abs"   => EvalAbs(cl, scope),
            "min"   => BinaryRealMath(cl, scope, Math.Min),
            "max"   => BinaryRealMath(cl, scope, Math.Max),
            "sign"  => EvalSign(cl, scope),
            _       => throw new UnknownFunctionException(cl.Name)
        };
    }

    private Value EvalIf(Expr[] args, Scope scope)
    {
        if (args.Length != 3)
            throw new ArityException("if", 3, args.Length);
        var cond = EvalExpr(args[0], scope);
        if (cond.Kind != ValueKind.Bool)
            throw new TypeErrorException($"if() condition must be Bool, got {cond.Kind}");
        return cond.AsBool() ? EvalExpr(args[1], scope) : EvalExpr(args[2], scope);
    }

    private Value CallUserFunction(UserFunction fn, Expr[] argExprs, Scope callSite)
    {
        if (argExprs.Length != fn.Parameters.Length)
            throw new ArityException(fn.Name, fn.Parameters.Length, argExprs.Length);

        // cycle detection on user-function calls
        var callKey = $"__fn__{fn.Name}";
        int existingIdx = _resolving.IndexOf(callKey);
        if (existingIdx >= 0)
        {
            var chain = string.Join(" → ", _resolving[existingIdx..]) + " → " + callKey;
            throw new CycleException(chain);
        }

        _resolving.Add(callKey);
        try
        {
            // bind arguments into a fresh scope whose parent is the *definition* scope
            // (user functions close over globals, not caller locals)
            var callScope = new Scope($"fn:{fn.Name}", callSite);
            for (int i = 0; i < fn.Parameters.Length; i++)
            {
                var argVal = EvalExpr(argExprs[i], callSite);
                // bind a literal value as its string representation so Resolve works
                // — but we bypass the expression path by injecting a pre-resolved value
                callScope.Bind(fn.Parameters[i], argVal.ToString()!);
                // pre-memoize to avoid re-parsing the value string
                _memo[$"{callScope.DebugName}::{fn.Parameters[i]}"] = argVal;
            }
            return EvalExpr(fn.BodyAst, callScope);
        }
        finally
        {
            _resolving.Remove(callKey);
        }
    }

    // ── Math helpers ─────────────────────────────────────────────────────────

    private Value UnaryMath(CallExpr cl, Scope scope,
        Func<double, double> realFn, Func<Complex, Complex> complexFn)
    {
        if (cl.Args.Length != 1) throw new ArityException(cl.Name, 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        return v.Kind == ValueKind.Real
            ? new Value(realFn(v.AsReal()))
            : new Value(complexFn(v.AsComplex()));
    }

    private Value BinaryMath(CallExpr cl, Scope scope,
        Func<double, double, double> realFn, Func<Complex, Complex, Complex> complexFn)
    {
        if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
        var a = EvalExpr(cl.Args[0], scope);
        var b = EvalExpr(cl.Args[1], scope);
        var kind = Value.CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(realFn(a.AsReal(), b.AsReal()))
            : new Value(complexFn(a.ToComplex(), b.ToComplex()));
    }

    private Value BinaryRealMath(CallExpr cl, Scope scope, Func<double, double, double> fn)
    {
        if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
        var a = EvalExpr(cl.Args[0], scope);
        var b = EvalExpr(cl.Args[1], scope);
        if (a.Kind != ValueKind.Real || b.Kind != ValueKind.Real)
            throw new TypeErrorException($"'{cl.Name}' requires real arguments");
        return new Value(fn(a.AsReal(), b.AsReal()));
    }

    private Value EvalAbs(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("abs", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        return v.Kind == ValueKind.Real
            ? new Value(Math.Abs(v.AsReal()))
            : new Value(Complex.Abs(v.AsComplex()));
    }

    private Value EvalSign(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("sign", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind != ValueKind.Real)
            throw new TypeErrorException("sign() requires a real argument");
        return new Value((double)Math.Sign(v.AsReal()));
    }

    // ── Domain-safe wrappers ─────────────────────────────────────────────────

    private static double SafeLog(double x)
    {
        if (x <= 0) throw new DomainException("log", $"argument {x} ≤ 0");
        return Math.Log(x);
    }

    private static double SafeLog10(double x)
    {
        if (x <= 0) throw new DomainException("log10", $"argument {x} ≤ 0");
        return Math.Log10(x);
    }

    private static double SafeSqrt(double x)
    {
        if (x < 0) throw new DomainException("sqrt", $"argument {x} < 0 (use Complex context)");
        return Math.Sqrt(x);
    }

    // ── Units ────────────────────────────────────────────────────────────────

    private static Value ApplyUnit(Value v, string? unit)
    {
        if (unit is null) return v;
        if (v.Kind == ValueKind.String)
            throw new TypeErrorException($"Cannot apply unit '{unit}' to a String value");
        var scale = Units.Scale(unit)
            ?? throw new ExpressionException($"Unknown unit '{unit}'");
        return v.Kind == ValueKind.Real
            ? new Value(v.AsReal() * scale)
            : new Value(v.AsComplex() * scale);
    }
}
