namespace CircuitRF.Core.Expressions;

/// <summary>
/// Generic SDD expression evaluator — one tree-walk, two scalar types (§2.2).
/// Eval&lt;SddScalar&gt; = plain value evaluation; Eval&lt;Dual&gt; = forward-mode AD.
/// No scope chain, no memos, no cycle detection — parameters are pre-resolved doubles,
/// port voltages are pre-seeded T values. SDD context is real-only (no j, no Complex).
/// </summary>
public static class SddEvaluator
{
    /// <summary>
    /// Evaluate an already-parsed SDD expression AST.
    /// </summary>
    /// <param name="ast">Parsed expression (from Parser.Parse — cached on the SDD model).</param>
    /// <param name="bindings">
    /// Map of name → T. Port voltages are T seeds; parameters are T.Constant(resolvedDouble).
    /// </param>
    /// <param name="modelName">Used in domain-error warnings.</param>
    public static T Eval<T>(
        Expr ast,
        IReadOnlyDictionary<string, T> bindings,
        string modelName = "<sdd>")
        where T : IAdScalar<T>
    {
        AdWarnings.CurrentModel = modelName;
        return EvalExpr(ast);

        T EvalExpr(Expr e) => e switch
        {
            NumberExpr n    => T.Constant(n.Value),
            ConstExpr c     => EvalConst(c.Name),
            RefExpr r       => EvalRef(r.Name),
            UnaryExpr u     => EvalUnary(u),
            BinaryExpr b    => EvalBinary(b),
            CompareExpr _   => throw new TypeErrorException("SDD context: comparison result is Bool, cannot use as scalar"),
            LogicExpr _     => throw new TypeErrorException("SDD context: logical result is Bool, cannot use as scalar"),
            ConditionalExpr cd => EvalConditional(cd),
            CallExpr cl     => EvalCall(cl),
            StringLiteralExpr _ => throw new TypeErrorException("SDD context: string literal not allowed in expression"),
            _               => throw new ExpressionException($"Unknown AST node: {e.GetType().Name}")
        };

        T EvalConst(string name) => name switch
        {
            "pi" => T.Constant(Math.PI),
            "e"  => T.Constant(Math.E),
            "j"  => throw new TypeErrorException("SDD context: imaginary unit 'j' not allowed (SDD equations are real-only)"),
            _    => throw new ExpressionException($"Unknown constant '{name}'")
        };

        T EvalRef(string name)
        {
            if (bindings.TryGetValue(name, out var v)) return v;
            throw new UnresolvedNameException(name, modelName);
        }

        T EvalUnary(UnaryExpr u) => u.Op switch
        {
            "-" => T.Neg(EvalExpr(u.Operand)),
            "+" => EvalExpr(u.Operand),
            "!" => throw new TypeErrorException("SDD context: '!' not allowed (SDD equations are real-only)"),
            _   => throw new ExpressionException($"Unknown unary op '{u.Op}'")
        };

        T EvalBinary(BinaryExpr b)
        {
            var l = EvalExpr(b.Left);
            var r = EvalExpr(b.Right);
            return b.Op switch
            {
                "+" => T.Add(l, r),
                "-" => T.Sub(l, r),
                "*" => T.Mul(l, r),
                "/" => T.Div(l, r),
                "^" => T.Pow(l, r),
                _   => throw new ExpressionException($"Unknown binary op '{b.Op}'")
            };
        }

        // Conditionals: evaluate condition using extracted doubles (§12.2).
        // AD takes the active-branch derivative; the other branch is not evaluated.
        T EvalConditional(ConditionalExpr cd)
        {
            bool cond = EvalConditionAsBool(cd.Condition);
            return cond ? EvalExpr(cd.Then) : EvalExpr(cd.Else);
        }

        // Evaluate a condition expression to a bool by extracting scalar values.
        // Only Compare and Logic nodes are valid condition expressions in SDD.
        bool EvalConditionAsBool(Expr cond) => cond switch
        {
            CompareExpr cmp => EvalCompareAsBool(cmp),
            LogicExpr lg    => EvalLogicAsBool(lg),
            UnaryExpr { Op: "!" } u => !EvalConditionAsBool(u.Operand),
            ConditionalExpr cd => EvalConditionAsBool(cd.Condition)
                ? EvalConditionAsBool(cd.Then)
                : EvalConditionAsBool(cd.Else),
            _ => throw new TypeErrorException($"SDD conditional: expected a comparison or logical expression, got {cond.GetType().Name}")
        };

        bool EvalCompareAsBool(CompareExpr cmp)
        {
            double l = T.ValueOf(EvalExpr(cmp.Left));
            double r = T.ValueOf(EvalExpr(cmp.Right));
            return cmp.Op switch
            {
                "<"  => l < r,
                "<=" => l <= r,
                ">"  => l > r,
                ">=" => l >= r,
                "==" => l == r,
                "!=" => l != r,
                _    => throw new ExpressionException($"Unknown comparison op '{cmp.Op}'")
            };
        }

        bool EvalLogicAsBool(LogicExpr lg)
        {
            bool l = EvalConditionAsBool(lg.Left);
            if (lg.Op == "&&" && !l) return false;
            if (lg.Op == "||" && l)  return true;
            bool r = EvalConditionAsBool(lg.Right);
            return lg.Op == "&&" ? l && r : l || r;
        }

        T EvalCall(CallExpr cl)
        {
            T Arg(int i)
            {
                if (cl.Args.Length <= i) throw new ArityException(cl.Name, i + 1, cl.Args.Length);
                return EvalExpr(cl.Args[i]);
            }

            switch (cl.Name)
            {
                case "exp":   RequireArity(cl, 1); return T.Exp(Arg(0));
                case "log":   // natural log (existing; kept for hero-equation compatibility)
                case "ln":    // unambiguous natural log (preferred new name per expressions.md §7)
                    RequireArity(cl, 1); return T.Log(Arg(0));
                case "sqrt":  RequireArity(cl, 1); return T.Sqrt(Arg(0));
                case "tanh":  RequireArity(cl, 1); return T.Tanh(Arg(0));
                case "sin":   RequireArity(cl, 1); return T.Sin(Arg(0));
                case "cos":   RequireArity(cl, 1); return T.Cos(Arg(0));
                case "abs":   RequireArity(cl, 1); return T.Abs(Arg(0));
                case "pow":   RequireArity(cl, 2); return T.Pow(Arg(0), Arg(1));
                case "sinh":  RequireArity(cl, 1);
                {
                    // sinh(x) = (exp(x) - exp(-x)) / 2
                    var x = Arg(0);
                    return T.Div(T.Sub(T.Exp(x), T.Exp(T.Neg(x))), T.Constant(2.0));
                }
                case "cosh":  RequireArity(cl, 1);
                {
                    // cosh(x) = (exp(x) + exp(-x)) / 2
                    var x = Arg(0);
                    return T.Div(T.Add(T.Exp(x), T.Exp(T.Neg(x))), T.Constant(2.0));
                }
                case "tan":   RequireArity(cl, 1);
                    return T.Div(T.Sin(Arg(0)), T.Cos(Arg(0)));
                case "log10": RequireArity(cl, 1);
                    return T.Div(T.Log(Arg(0)), T.Constant(Math.Log(10.0)));
                case "min":   RequireArity(cl, 2);
                {
                    var a = Arg(0); var b = Arg(1);
                    return T.ValueOf(a) <= T.ValueOf(b) ? a : b;
                }
                case "max":   RequireArity(cl, 2);
                {
                    var a = Arg(0); var b = Arg(1);
                    return T.ValueOf(a) >= T.ValueOf(b) ? a : b;
                }
                case "sign":  RequireArity(cl, 1);
                    return T.Constant(Math.Sign(T.ValueOf(Arg(0))));
                case "if":
                {
                    if (cl.Args.Length != 3) throw new ArityException("if", 3, cl.Args.Length);
                    bool cond = EvalConditionAsBool(cl.Args[0]);
                    return cond ? EvalExpr(cl.Args[1]) : EvalExpr(cl.Args[2]);
                }
                case "atan":  RequireArity(cl, 1);
                    return T.Constant(Math.Atan(T.ValueOf(Arg(0))));
                case "atan2": RequireArity(cl, 2);
                    return T.Constant(Math.Atan2(T.ValueOf(Arg(0)), T.ValueOf(Arg(1))));
                case "asin":  RequireArity(cl, 1);
                    return T.Constant(Math.Asin(T.ValueOf(Arg(0))));
                case "acos":  RequireArity(cl, 1);
                    return T.Constant(Math.Acos(T.ValueOf(Arg(0))));
                default:
                    throw new UnknownFunctionException(cl.Name);
            }
        }

        static void RequireArity(CallExpr cl, int expected)
        {
            if (cl.Args.Length != expected)
                throw new ArityException(cl.Name, expected, cl.Args.Length);
        }
    }

    /// <summary>
    /// Convenience: build a binding dictionary from parameter doubles + port voltage doubles,
    /// evaluate the expression, and return the scalar value.
    /// Port voltage names: "_v1", "_v2", ... (SDD convention).
    /// </summary>
    public static double EvalDouble(
        Expr ast,
        IReadOnlyDictionary<string, double> parameters,
        double[] portVoltages,
        string modelName = "<sdd>")
    {
        var bindings = new Dictionary<string, SddScalar>(StringComparer.Ordinal);
        foreach (var kv in parameters)
            bindings[kv.Key] = SddScalar.Constant(kv.Value);
        for (int i = 0; i < portVoltages.Length; i++)
            bindings[$"_v{i + 1}"] = SddScalar.Constant(portVoltages[i]);

        return Eval<SddScalar>(ast, bindings, modelName).Value;
    }

    /// <summary>
    /// Evaluate the expression in Dual arithmetic, returning value and full gradient.
    /// Gradient[k] = ∂result/∂portVoltages[k].
    /// </summary>
    public static (double Value, double[] Grad) EvalDual(
        Expr ast,
        IReadOnlyDictionary<string, double> parameters,
        double[] portVoltages,
        string modelName = "<sdd>")
    {
        int n = portVoltages.Length;
        if (n > Dual.MaxN)
            throw new ArgumentException($"Port count {n} exceeds Dual.MaxN = {Dual.MaxN}");

        var bindings = new Dictionary<string, Dual>(StringComparer.Ordinal);
        foreach (var kv in parameters)
            bindings[kv.Key] = Dual.Param(kv.Value, n);
        for (int i = 0; i < n; i++)
            bindings[$"_v{i + 1}"] = Dual.Seed(portVoltages[i], n, i);

        var result = Eval<Dual>(ast, bindings, modelName);
        var grad = new double[n];
        for (int i = 0; i < n; i++) grad[i] = result.GetGrad(i);
        return (result.Value, grad);
    }

    /// <summary>
    /// Evaluate the expression in Dual arithmetic with control currents.
    /// controlCurrents[i] = (N=1-based control index, Value=current value).
    /// Returns (value, grad) where grad has length portCount + controlCount:
    ///   grad[0..nV-1]      = ∂result/∂portVoltages[k]
    ///   grad[nV..nV+nC-1]  = ∂result/∂controlCurrents[k].Value
    /// </summary>
    public static (double Value, double[] Grad) EvalDual(
        Expr ast,
        IReadOnlyDictionary<string, double> parameters,
        double[] portVoltages,
        (int N, double Value)[] controlCurrents,
        string modelName = "<sdd>")
    {
        int nV = portVoltages.Length;
        int nC = controlCurrents.Length;
        int n  = nV + nC;
        if (n > Dual.MaxN)
            throw new ArgumentException(
                $"Port count {nV} + control count {nC} = {n} exceeds Dual.MaxN = {Dual.MaxN}; " +
                $"reduce SDD port count or number of C[n] references");

        var bindings = new Dictionary<string, Dual>(StringComparer.Ordinal);
        foreach (var kv in parameters)
            bindings[kv.Key] = Dual.Param(kv.Value, n);
        for (int i = 0; i < nV; i++)
            bindings[$"_v{i + 1}"] = Dual.Seed(portVoltages[i], n, i);
        for (int i = 0; i < nC; i++)
            bindings[$"_c{controlCurrents[i].N}"] = Dual.Seed(controlCurrents[i].Value, n, nV + i);

        var result = Eval<Dual>(ast, bindings, modelName);
        var grad = new double[n];
        for (int i = 0; i < n; i++) grad[i] = result.GetGrad(i);
        return (result.Value, grad);
    }
}
