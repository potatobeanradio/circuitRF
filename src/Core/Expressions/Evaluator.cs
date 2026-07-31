using System.Numerics;
using RfCore.Data;

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
    // optional measurement context (analysis results for qualified accessors)
    private readonly MeasurementContext? _ctx;

    public Evaluator() { }
    public Evaluator(MeasurementContext ctx) => _ctx = ctx;

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
        IndexExpr       ix => EvalIndex(ix, scope),
        _ => throw new ExpressionException($"Unknown AST node: {expr.GetType().Name}")
    };

    /// <summary>Parse and evaluate an expression string in the given scope.</summary>
    public Value Eval(string expression, Scope scope, string? unit = null)
    {
        var ast = Parser.Parse(expression);
        var raw = EvalExpr(ast, scope);
        // var-unit-wins: if any referenced variable declares its own unit, that unit was already
        // applied when the variable resolved (Resolve → ApplyUnit with the binding's unit); do NOT
        // re-apply the site unit. Matches FreqUnit.ResolveHz, using per-binding scope units.
        if (!string.IsNullOrEmpty(unit) && ReferencesUnitBearingVar(ast, scope))
            return raw;
        return ApplyUnit(raw, unit);
    }

    private static bool ReferencesUnitBearingVar(Expr ast, Scope scope)
    {
        foreach (var name in AstWalker.CollectRefs(ast))
        {
            var found = scope.Lookup(name);
            if (found is not null && !string.IsNullOrEmpty(found.Value.Unit))
                return true;
        }
        return false;
    }

    // ── Nodes ────────────────────────────────────────────────────────────────

    private static Value EvalConst(string name) => name switch
    {
        "j"   => Value.J,
        "pi"  => Value.Pi,
        "e"   => Value.E,
        "All" => Value.AllSentinel,
        _     => throw new ExpressionException($"Unknown constant '{name}'")
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
        // qualified accessor: "Analysis.CubeName(args)" e.g. "HB1.V"
        if (cl.Name.Contains('.') && _ctx != null)
            return EvalQualifiedAccessor(cl, scope);

        // user-defined function?
        if (_functions.TryGetValue(cl.Name, out var ufn))
            return CallUserFunction(ufn, cl.Args, scope);

        // built-ins — cube-aware variants handle DataCube args; scalars fall through to normal math
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
            "log"   => EvalLog(cl, scope),         // natural log, cube-aware
            "ln"    => EvalLog(cl, scope),          // alias
            "log10" => EvalLog10(cl, scope),        // cube-aware
            "sqrt"  => UnaryMath(cl, scope, SafeSqrt, Complex.Sqrt),
            "pow"   => BinaryMath(cl, scope, Math.Pow,  Complex.Pow),
            // Rounding family. Applied componentwise for Complex, which is the conventional
            // extension and keeps them total rather than throwing on a complex argument.
            "floor" => UnaryMath(cl, scope, Math.Floor,    z => new Complex(Math.Floor(z.Real), Math.Floor(z.Imaginary))),
            "ceil"  => UnaryMath(cl, scope, Math.Ceiling,  z => new Complex(Math.Ceiling(z.Real), Math.Ceiling(z.Imaginary))),
            "round" => UnaryMath(cl, scope, x => Math.Round(x, MidpointRounding.AwayFromZero),
                                 z => new Complex(Math.Round(z.Real, MidpointRounding.AwayFromZero),
                                                  Math.Round(z.Imaginary, MidpointRounding.AwayFromZero))),
            "int"   => UnaryMath(cl, scope, Math.Truncate, z => new Complex(Math.Truncate(z.Real), Math.Truncate(z.Imaginary))),
            "abs"       => EvalAbs(cl, scope),
            "min"       => BinaryRealMath(cl, scope, Math.Min),
            "max"       => BinaryRealMath(cl, scope, Math.Max),
            "sign"      => EvalSign(cl, scope),
            // Complex / cube helpers (expressions.md §7 + measurements §3.1)
            "conj"      => EvalConj(cl, scope),
            "real"      => EvalReal(cl, scope),
            "imag"      => EvalImag(cl, scope),
            "mag"       => EvalMag(cl, scope),
            "phase"     => EvalPhase(cl, scope),
            "phase_rad" => EvalPhaseRad(cl, scope),
            "polar"     => EvalPolar(cl, scope),
            "dB"        => EvalDB20(cl, scope),     // 20·log10|z|
            "dB20"      => EvalDB20(cl, scope),     // alias: 20·log10|z|
            "dB10"      => EvalDB10(cl, scope),     // 10·log10|z|
            "dBm"       => EvalDBm(cl, scope),      // 10·log10(|z|/1e-3)
            _           => throw new UnknownFunctionException(cl.Name)
        };
    }

    // ── Qualified cube accessor: Analysis.CubeName(args) ────────────────────

    private Value EvalQualifiedAccessor(CallExpr cl, Scope scope)
    {
        var dot          = cl.Name.IndexOf('.');
        var analysisName = cl.Name[..dot];
        var accessorName = cl.Name[(dot + 1)..];  // "V", "INl", "I", "S", …

        var ds = _ctx!.GetAnalysis(analysisName);

        if (cl.Args.Length == 0)
            return new Value(ds[accessorName]);

        // ── S(portI, portJ) — S-parameter pair (1-based port numbers) ─────────
        if (accessorName == "S")
        {
            if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
            int pi = (int)EvalExpr(cl.Args[0], scope).AsReal();
            int pj = (int)EvalExpr(cl.Args[1], scope).AsReal();
            // DataSet.S resolves i/j by axis value (1-based) and keeps freq (+ sweep), order-independent —
            // unlike the previous positional slice, which assumed [i, j, freq] and pinned freq instead.
            return new Value(ds.S(pi, pj));
        }

        // ── V(nodeName,…) / INl(nodeName,…) — node-indexed ─────────────────
        // ── I(branchName,…) — branch-indexed (unified I cube, branch axis) ───
        if (accessorName is "V" or "INl" or "I")
        {
            var cube = ds[accessorName];
            string axisName = accessorName == "I" ? "branch" : "node";
            var nameVal = EvalExpr(cl.Args[0], scope);
            string label = nameVal.Kind == ValueKind.String
                ? nameVal.AsString()
                : nameVal.ToString();

            // Find the named axis; sweep axes (from ParametricSweepEngine) are prepended.
            // Layout: [sweep0, ..., sweepN, node/branch, harmonic/mixIndex]
            int axisIdx = -1;
            for (int a = 0; a < cube.Rank; a++)
                if (cube.Axes[a].Name == axisName) { axisIdx = a; break; }
            if (axisIdx < 0) axisIdx = 0;  // fallback: single-point, target axis is first

            var labels = cube.Axes[axisIdx].Labels;
            int idx    = labels is null ? -1
                : Array.FindIndex(labels, s => s.Equals(label, StringComparison.OrdinalIgnoreCase));

            // V only: linear-interior node — fall back to the linear back-solver.
            if (idx < 0 && accessorName == "V" &&
                _ctx.TryGetBackSolver(analysisName, out var bs))
                return EvalVFromBackSolver(bs!, cube, cl.Args, scope, cl.Name, label);

            if (idx < 0)
                throw new ExpressionException(
                    $"{cl.Name}: {axisName} '{label}' not found. " +
                    $"Available: [{string.Join(", ", labels ?? [])}]");

            // Build slice: sweep axes get args[2+a]; node/branch gets idx; harm gets args[1].
            // Caller convention: V/I("name", harmSlice, sweepSlice0, sweepSlice1, ...)
            int numSweepAxes = axisIdx;
            var sliceArgs = new object[cube.Rank];

            sliceArgs[axisIdx] = (object)idx;

            if (axisIdx + 1 < cube.Rank)
                sliceArgs[axisIdx + 1] = cl.Args.Length > 1
                    // Spectral axis (harmonic / mixIndex): accept an integer index, "All", OR a quoted
                    // label — e.g. the two-tone mixIndex tag "(1,-1)" — so V("Vout", "(1,-1)") works.
                    ? ResolveSpectralArg(EvalExpr(cl.Args[1], scope), cube.Axes[axisIdx + 1])
                    : (object)Range.All;

            for (int a = 0; a < numSweepAxes; a++)
            {
                int argIdx = 2 + a;
                sliceArgs[a] = argIdx < cl.Args.Length
                    ? ArgToSliceObj(EvalExpr(cl.Args[argIdx], scope), cl.Name, argIdx)
                    : (object)Range.All;
            }

            return SliceToValue(cube[sliceArgs]);
        }

        // ── Generic positional accessor ───────────────────────────────────────
        {
            var cube      = ds[accessorName];
            var sliceArgs = new object[cl.Args.Length];
            for (int i = 0; i < cl.Args.Length; i++)
                sliceArgs[i] = ArgToSliceObj(EvalExpr(cl.Args[i], scope), cl.Name, i);
            return SliceToValue(cube[sliceArgs]);
        }
    }

    // ── V back-solve from linear back-solver (C1) ────────────────────────────

    private Value EvalVFromBackSolver(
        ILinearBackSolver bs, DataCube vCube, Expr[] args, Scope scope,
        string exprName, string nodeName)
    {
        if (!bs.TryGetNodeNumber(nodeName, out int circNode))
            throw new ExpressionException(
                $"{exprName}: node '{nodeName}' not found in stored cube or netlist.");

        // args[1] = harmonic (scalar or All), args[2] = sweep slice (scalar, All, or absent)
        var harmVal  = args.Length > 1 ? EvalExpr(args[1], scope) : Value.AllSentinel;
        var sweepVal = args.Length > 2 ? EvalExpr(args[2], scope) : Value.AllSentinel;

        int nSweep = bs.SweepCount;

        // Scalar harmonic + All sweep → 1D cube [Pin] (or scalar if no-sweep)
        if (harmVal.Kind == ValueKind.Real && sweepVal.Kind == ValueKind.All)
        {
            int k = (int)harmVal.AsReal();
            if (vCube.Rank < 3)
            {
                // No-sweep: return scalar
                return new Value(bs.GetNodeVoltage(circNode, k, 0));
            }
            var pinAxis = vCube.Axes[2];
            var data    = new System.Numerics.Complex[nSweep];
            for (int si = 0; si < nSweep; si++)
                data[si] = bs.GetNodeVoltage(circNode, k, si);
            return new Value(new RfCore.Data.DataCube([pinAxis], data));
        }

        // Scalar harmonic + scalar sweep → Complex scalar
        if (harmVal.Kind == ValueKind.Real && sweepVal.Kind == ValueKind.Real)
        {
            int k  = (int)harmVal.AsReal();
            int si = (int)sweepVal.AsReal();
            return new Value(bs.GetNodeVoltage(circNode, k, si));
        }

        // All harmonics + All sweep → 2D cube [harmonic, Pin] (or 1D [harmonic] if no-sweep)
        if (harmVal.Kind == ValueKind.All && sweepVal.Kind == ValueKind.All)
        {
            var harmAxis = vCube.Axes[1];
            int K1       = harmAxis.Values.Length;
            if (vCube.Rank < 3)
            {
                var data1 = new System.Numerics.Complex[K1];
                for (int k = 0; k < K1; k++)
                    data1[k] = bs.GetNodeVoltage(circNode, k, 0);
                return new Value(new RfCore.Data.DataCube([harmAxis], data1));
            }
            var pinAxis2 = vCube.Axes[2];
            var data2    = new System.Numerics.Complex[K1 * nSweep];
            for (int k = 0; k < K1; k++)
            for (int si = 0; si < nSweep; si++)
                data2[k * nSweep + si] = bs.GetNodeVoltage(circNode, k, si);
            return new Value(new RfCore.Data.DataCube([harmAxis, pinAxis2], data2));
        }

        throw new ExpressionException(
            $"{exprName}: V back-solve: unsupported argument combination " +
            $"(harmonic kind={harmVal.Kind}, sweep kind={sweepVal.Kind}). " +
            "Use a scalar index or All for each axis.");
    }

    // ── Shared slice-result → Value conversion ────────────────────────────────

    private static Value SliceToValue(SliceResult sr)
    {
        if (sr.IsCube)    return new Value(sr.Cube!);
        if (sr.IsComplex) return new Value(sr.ComplexValue!.Value);
        return new Value(sr.RealValue!.Value);
    }

    private static object ArgToSliceObj(Value v, string name, int idx) => v.Kind switch
    {
        ValueKind.All  => (object)Range.All,
        ValueKind.Real => (object)(int)v.AsReal(),
        _ => throw new ExpressionException(
            $"{name}: argument {idx} must be an integer index or All, got {v.Kind}")
    };

    /// <summary>Resolves a spectral-axis (harmonic / mixIndex) accessor argument: <c>All</c> keeps the
    /// axis; an integer is an index; a quoted string is a label (e.g. the mixIndex tag "(1,-1)").</summary>
    private static object ResolveSpectralArg(Value v, RfCore.Data.Axis axis)
        => v.Kind == ValueKind.All ? (object)Range.All : ResolvePin(v, axis);

    // ── Positional cube index: Target[token, …]  (numpy-style; mirrors the accessor) ──────────

    private Value EvalIndex(IndexExpr ix, Scope scope)
    {
        var target = EvalExpr(ix.Target, scope);
        if (target.Kind != ValueKind.Cube)
            throw new ExpressionException(
                $"'[...]' indexing requires a cube (e.g. HB1.V[...]); got {target.Kind}.");
        var cube = target.AsCube();

        if (ix.Tokens.Length != cube.Rank)
            throw new ExpressionException(
                $"Cube index has {ix.Tokens.Length} token(s) but cube has {cube.Rank} axis/axes " +
                $"[{string.Join(", ", cube.Axes.Select(a => a.Name))}]. Brackets are positional " +
                "(cube-axis order): ':' keeps an axis, a name/index fixes it, 'a:b' is a range.");

        var args = new object[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var tok  = ix.Tokens[d];
            var axis = cube.Axes[d];
            switch (tok.Kind)
            {
                case IndexTokenKind.Whole:
                    args[d] = Range.All;
                    break;
                case IndexTokenKind.Range:
                    int lo = (int)EvalExpr(tok.A!, scope).AsReal();
                    int hi = (int)EvalExpr(tok.B!, scope).AsReal();
                    args[d] = new Range(lo, hi);
                    break;
                default: // Pin
                    args[d] = ResolvePin(EvalExpr(tok.A!, scope), axis);
                    break;
            }
        }
        return SliceToValue(cube[args]);
    }

    private static object ResolvePin(Value v, RfCore.Data.Axis axis)
    {
        if (v.Kind == ValueKind.String)
        {
            string label = v.AsString();
            if (axis.Labels is null)
                throw new ExpressionException(
                    $"Axis '{axis.Name}' has no name labels — cannot resolve \"{label}\".");
            int idx = Array.FindIndex(axis.Labels, s => s.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new ExpressionException(
                    $"'{label}' not found on axis '{axis.Name}'. Available: [{string.Join(", ", axis.Labels)}].");
            return idx;
        }
        if (v.Kind == ValueKind.Real)
        {
            int n = (int)v.AsReal();
            // S/Y/Z port axes (i, j) use 1-based PORT NUMBERS resolved by axis value: S[:, 2, 1] = S21.
            if (axis.Name is "i" or "j")
            {
                for (int k = 0; k < axis.Values.Length; k++)
                    if ((int)Math.Round(axis.Values[k]) == n) return k;
                throw new ExpressionException(
                    $"Port {n} not found on axis '{axis.Name}'. Available ports: " +
                    $"[{string.Join(", ", axis.Values.Select(x => ((int)Math.Round(x)).ToString()))}].");
            }
            return n;
        }
        throw new ExpressionException(
            $"Index for axis '{axis.Name}' must be ':', a name, an integer, or a range — got {v.Kind}.");
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

    // ── Complex helpers (expressions.md §7) ─────────────────────────────────

    private Value EvalReal(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("real", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Real());
        return v.Kind == ValueKind.Real ? v : new Value(v.AsComplex().Real);
    }

    private Value EvalImag(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("imag", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Imag());
        return v.Kind == ValueKind.Real ? new Value(0.0) : new Value(v.AsComplex().Imaginary);
    }

    private Value EvalMag(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("mag", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Mag());
        return v.Kind == ValueKind.Real ? new Value(Math.Abs(v.AsReal())) : new Value(Complex.Abs(v.AsComplex()));
    }

    private Value EvalPhase(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("phase", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Phase(degrees: true));
        return v.Kind == ValueKind.Real
            ? new Value(v.AsReal() >= 0 ? 0.0 : 180.0)
            : new Value(v.AsComplex().Phase * (180.0 / Math.PI));
    }

    private Value EvalPhaseRad(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("phase_rad", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Phase(degrees: false));
        return v.Kind == ValueKind.Real
            ? new Value(v.AsReal() >= 0 ? 0.0 : Math.PI)
            : new Value(v.AsComplex().Phase);
    }

    private Value EvalPolar(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException("polar", 2, cl.Args.Length);
        var r   = EvalExpr(cl.Args[0], scope);
        var deg = EvalExpr(cl.Args[1], scope);
        if (r.Kind != ValueKind.Real)   throw new TypeErrorException("polar() magnitude must be Real");
        if (deg.Kind != ValueKind.Real) throw new TypeErrorException("polar() phase must be Real");
        return new Value(Complex.FromPolarCoordinates(r.AsReal(), deg.AsReal() * Math.PI / 180.0));
    }

    private Value EvalSign(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("sign", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind != ValueKind.Real)
            throw new TypeErrorException("sign() requires a real argument");
        return new Value((double)Math.Sign(v.AsReal()));
    }

    // ── Cube-aware complex/dB helpers (measurements §3.1) ────────────────────

    private Value EvalConj(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("conj", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube)  return new Value(v.AsCube().Conj());
        if (v.Kind == ValueKind.Real)   return v;
        return new Value(Complex.Conjugate(v.AsComplex()));
    }

    private Value EvalLog(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException(cl.Name, 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Ln());
        return v.Kind == ValueKind.Real
            ? new Value(SafeLog(v.AsReal()))
            : new Value(Complex.Log(v.AsComplex()));
    }

    private Value EvalLog10(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("log10", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Log10());
        return v.Kind == ValueKind.Real
            ? new Value(SafeLog10(v.AsReal()))
            : new Value(Complex.Log(v.AsComplex(), 10));
    }

    private Value EvalDB20(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("dB", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().DB20());
        var mag = v.Kind == ValueKind.Real ? Math.Abs(v.AsReal()) : v.AsComplex().Magnitude;
        return new Value(20.0 * Math.Log10(mag + 1e-300));
    }

    private Value EvalDB10(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("dB10", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().DB10());
        var mag = v.Kind == ValueKind.Real ? Math.Abs(v.AsReal()) : v.AsComplex().Magnitude;
        return new Value(10.0 * Math.Log10(mag + 1e-300));
    }

    private Value EvalDBm(CallExpr cl, Scope scope)
    {
        // dBm(p) = 10*log10(|p| / 1e-3) = 10*log10(|p|) + 30
        if (cl.Args.Length != 1) throw new ArityException("dBm", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().DB10() + 30.0);
        var mag = v.Kind == ValueKind.Real ? Math.Abs(v.AsReal()) : v.AsComplex().Magnitude;
        return new Value(10.0 * Math.Log10(mag / 1e-3 + 1e-300));
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
        // Linear-scale units (uF, nH, GHz, …) multiply the value.
        // Identity/measurement units (V, A, W, dBm, dB, …) carry no scale multiplier:
        // the value is already in the base unit, so treat them as scale = 1.0.
        double scale = Units.Scale(unit)
            ?? (Units.IsRecognizedUnit(unit) ? 1.0
                : throw new ExpressionException($"Unknown unit '{unit}'"));
        if (v.Kind == ValueKind.Cube)
            return scale == 1.0 ? v : Value.Mul(v, new Value(scale));
        return v.Kind == ValueKind.Real
            ? new Value(v.AsReal() * scale)
            : new Value(v.AsComplex() * scale);
    }
}
