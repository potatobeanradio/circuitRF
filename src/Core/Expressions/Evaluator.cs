using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Walks a scope chain, evaluates AST nodes, enforces cycle detection and memoization (§10).
/// One Evaluator instance per elaboration run; holds the resolving-stack and memo cache.
/// </summary>
public sealed partial class Evaluator
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
    /// Where <c>validrange</c> reports a value outside its range; null reports nothing. The elaborator points it at the
    /// netlist's warnings, so a fitted model used outside the range it was fitted over says so once per instance.
    /// </summary>
    public Action<string>? Warn { get; set; }

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
        PushStatFrame(owningScope, name);   // a distribution in this binding draws on the binding's own stream
        try
        {
            // A binding's own unit is a site unit like any other, and var-unit-wins applies to it: `b = 2*a`
            // declared in mil, with `a` in mil, is 20 mil — `a` is already scaled, and scaling it again made b
            // 2.54e-5 times too small. Only a binding that is not unit-bearing takes its unit on the result; one
            // that is gives it to its bare additive operands instead (`b = a + 5` in mil is a + 5 mil).
            var val  = EvalAtSite(Parser.Parse(expression), owningScope, unit);
            _memo[key] = val;
            return val;
        }
        finally
        {
            PopStatFrame();
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
        PortRangeExpr      => RefusePortRange(),
        _ => throw new ExpressionException($"Unknown AST node: {expr.GetType().Name}")
    };

    /// <summary>Parse and evaluate an expression string in the given scope.</summary>
    public Value Eval(string expression, Scope scope, string? unit = null)
        => EvalAtSite(Parser.Parse(expression), scope, unit);

    // var-unit-wins (expressions.md §8): a unit-bearing expression — a unit literal, or a name that carries a unit and
    // was scaled when it resolved — is already base SI, so the site unit is not applied to the result; its bare additive
    // operands take it instead (Evaluator.SiteUnit.cs). Anything else takes the site unit on its result, as always.
    private Value EvalAtSite(Expr ast, Scope scope, string? unit)
    {
        if (!string.IsNullOrEmpty(unit) && IsUnitBearing(ast, scope))
            return EvalExpr(ScaleBareOperands(ast, scope, unit), scope);
        return ApplyUnit(EvalExpr(ast, scope), unit);
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
        if (l.Kind == ValueKind.Cube) return Value.LogicCubes(l, EvalExpr(lg.Right, scope), lg.Op);
        if (l.Kind != ValueKind.Bool)
            throw new TypeErrorException($"Operator '{lg.Op}' requires Bool, got {l.Kind}");
        if (lg.Op == "&&" && !l.AsBool()) return Value.False;
        if (lg.Op == "||" && l.AsBool())  return Value.True;
        var r = EvalExpr(lg.Right, scope);
        if (r.Kind == ValueKind.Cube) return Value.LogicCubes(l, r, lg.Op);
        if (r.Kind != ValueKind.Bool)
            throw new TypeErrorException($"Operator '{lg.Op}' requires Bool, got {r.Kind}");

        // ONCE THE SHORT-CIRCUIT HAS NOT FIRED, THE RESULT IS THE RIGHT OPERAND — for both
        // operators, and for the same reason: `&&` got here only with a true left (so the answer is
        // the right one), and `||` only with a false left (so the answer is the right one again).
        //
        // This used to return And(l, r), which is right for `&&` and silently wrong for `||`:
        // `false || true` came back FALSE. Nothing failed and nothing was reported — a conditional
        // simply took its else-branch, everywhere, whenever the first alternative did not hold.
        return r;
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

        // The WSProbe derived metrics (Evaluator.Wsp.cs) — the reference document's own function
        // library, registered under the document's own names. Routed before the table below rather
        // than folded into it because there are three dozen of them and every one is a cube map.
        if (IsWspBuiltin(cl.Name)) return EvalWspCall(cl, scope);

        // The network metrics, group delay and VSWR (Evaluator.Network.cs) — one goal expression away
        // from every quantity the Data Display derives from an S cube.
        if (IsNetworkBuiltin(cl.Name)) return EvalNetworkCall(cl, scope);

        // A two-port's gain and stability circles (Evaluator.Circles.cs, brief NZ-2), beside ns_circle.
        if (IsCircleBuiltin(cl.Name)) return EvalCircleCall(cl, scope);

        // The field measurements over a fields analysis (Evaluator.Fields.cs, brief-sar-10).
        if (IsFieldMetric(cl.Name)) return EvalFieldMetric(cl, scope);

        // The distribution functions (Evaluator.Statistical.cs): their nominal outside a trial, a draw inside one.
        // `limit` with three arguments is the clamp, told apart by its arity.
        if (IsStatisticalCall(cl)) return EvalStatistical(cl, scope);
        if (cl.Name == "limit")    return EvalClamp(cl, scope);
        if (cl.Name == "validrange") return EvalValidRange(cl, scope);

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
            "sqrt"  => EvalSqrt(cl, scope),         // cube-aware
            "pow"   => PowCall(cl, scope),
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
            // Rectangular counterpart of polar(). The natural way to write a frequency-dependent
            // immittance (`complex(G, w*C)`), and the form FreqDeferral renders a resolved Complex
            // back into — so a deferred expression can be re-parsed by the model that evaluates it.
            "complex"   => EvalComplex(cl, scope),
            // Cube-only: pin one axis BY NAME (measurements.md §3.2). Not a scalar function — it is
            // the shape-independent counterpart of a positional slice.
            "at"        => EvalAt(cl, scope),
            // Band reductions (measurements.md "Spec checks"): the worst value of a real quantity over a
            // RANGE of one axis — the frequency band a spec line names. The axis is reduced away; every
            // other axis (a parametric sweep, say) is kept, so a swept run gives one worst case per point.
            // ... and the statistics of a sample (Evaluator.Reductions.cs), over the same axis rule — by default a
            // Monte Carlo run's `trial` axis.
            "max_over"  => EvalReduceOver(cl, scope, "max_over", max: true),
            "min_over"  => EvalReduceOver(cl, scope, "min_over", max: false),
            "mean_over" or "std_over" or "median_over" or "skew_over" or "kurt_over" or "yield_over"
                        => EvalStatisticOver(cl, scope),
            "pctl_over" => EvalPercentileOver(cl, scope),
            "unwrap"    => EvalUnwrap(cl, scope),
            "deriv"     => EvalDeriv(cl, scope),
            "cpk"       => EvalCpk(cl, scope),
            "sigma_to"  => EvalSigmaTo(cl, scope),
            "histogram" => EvalHistogram(cl, scope),
            "cdf"       => EvalCdf(cl, scope),
            "normq"     => EvalNormq(cl, scope),
            "yield_sens" => EvalYieldSens(cl, scope),
            "dB"        => EvalDB20(cl, scope),     // 20·log10|z|
            "dB20"      => EvalDB20(cl, scope),     // alias: 20·log10|z|
            "dB10"      => EvalDB10(cl, scope),     // 10·log10|z|
            "dBm"       => EvalDBm(cl, scope),      // 10·log10(|z|/1e-3)
            _           => EvalCallOnPortMatrix(cl, scope)
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

        // ── S(1:2, 1:2) — a block of ports as a matrix of its own (Evaluator.PortBlock.cs) ──
        if (HasPortRange(cl) && ds.HasPortAxes(accessorName))
            return EvalPortSelection(ds[accessorName], cl, cl.Name, scope);

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

        // ── Y(i, j), Z(i, j), a TRL fixture's SA(i, j): any cube with port axes, by port number, as S ──
        if (cl.Args.Length == 2 && ds.HasPortAxes(accessorName))
        {
            int pi = (int)EvalExpr(cl.Args[0], scope).AsReal();
            int pj = (int)EvalExpr(cl.Args[1], scope).AsReal();
            return new Value(ds.PortTrace(accessorName, pi, pj));
        }

        // ── P("probe"[, k]) / Pt("probe") — power through an IProbe (brief ENV-MEAS-2) ──
        // Taken when the first argument is a string and no cube is literally named P / Pt; a DataSet that
        // holds one keeps the generic positional path below (no analysis emits one).
        if (accessorName is "P" or "Pt" && cl.Args.Length is 1 or 2 && !ds.Contains(accessorName))
        {
            var probeVal = EvalExpr(cl.Args[0], scope);
            if (probeVal.Kind == ValueKind.String)
                return EvalProbePower(cl, scope, ds, analysisName, accessorName, probeVal.AsString());
        }

        // ── V(nodeName,…) / INl(nodeName,…) — node-indexed ─────────────────
        // ── I(branchName,…) — branch-indexed (unified I cube, branch axis) ───
        // ── OP(name,…)     — a compact model's own operating-point variable ──
        //
        // OP joins this branch rather than getting one of its own because it IS this shape: one
        // labelled axis, optionally a spectral axis, optionally sweep axes in front. The label is
        // "<InstancePath>.<opVarName>" — the model's own spelling for the quantity, on circuitRF's
        // own address for the instance — and it is matched whole, never split on the dot.
        //
        // QUALIFIED ONLY, deliberately: DC1.OP("X1.gm"). There is no unqualified spelling, so a
        // testbench that runs two analyses never has to be guessed about.
        if (accessorName is "V" or "INl" or "I" or "OP")
        {
            var cube = ds[accessorName];
            string axisName = accessorName switch
            {
                "I"  => "branch",
                "OP" => "opvar",
                _    => "node",
            };
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

        // ── WSProbe accessors (brief-wsprobe-1 R-wsp1-11; overview D-5) ──────
        //
        //   SP1.wsp(r, c)     one element of the document's wsp matrix traced over freq (Eq. 36):
        //                     r and c are the document's 1-BASED row/col, matched by axis VALUE —
        //                     never positional, because wsp(3, 13) is a name the reference document
        //                     gave the quantity, and a positional slice of {freq,row,col} would
        //                     pin freq instead.
        //   SP1.idx("GATE")   the probe's idx, from __WspProbes (never guessed).
        //   SP1.H0("GATE") … SP1.F("GATE")   the per-probe default-output cubes, keyed by label
        //                     exactly as I("IP1") keys a branch — trailing sweep axes kept.
        //   SP1.SM_Y0("GATE"), SP1.SM_H0("GATE")   the two stability margins (WSP-9 R-wsp9-1),
        //                     Real over {freq}, resolved by the same route.
        if (accessorName == "wsp" && cl.Args.Length == 2)
        {
            var cube = ds["wsp"];
            int r = (int)Math.Round(EvalExpr(cl.Args[0], scope).AsReal());
            int c = (int)Math.Round(EvalExpr(cl.Args[1], scope).AsReal());
            var sliceArgs = new object[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                var ax = cube.Axes[d];
                sliceArgs[d] = ax.Name switch
                {
                    "row" => WspAxisIndex(ax, r, cl.Name, "row"),
                    "col" => WspAxisIndex(ax, c, cl.Name, "col"),
                    _     => Range.All,
                };
            }
            return SliceToValue(cube[sliceArgs]);
        }

        if (accessorName is "idx" or "H0" or "Y0" or "ZG" or "ZL" or "LG" or "F"
                         or "SM_Y0" or "SM_H0"
            && cl.Args.Length == 1)
        {
            var nameVal  = EvalExpr(cl.Args[0], scope);
            string label = nameVal.Kind == ValueKind.String ? nameVal.AsString() : nameVal.ToString();

            if (!ds.Contains("__WspProbes"))
                throw new ExpressionException(
                    $"{cl.Name}: analysis '{analysisName}' carries no WSProbe outputs (no WSProbe in the netlist).");
            var probes = ds["__WspProbes"];
            var labels = probes.Axes[0].Labels ?? [];
            int pi = Array.FindIndex(labels, l => l.Equals(label, StringComparison.Ordinal));
            if (pi < 0)
                throw new ExpressionException(
                    $"{cl.Name}: no WSProbe named '{label}'. Probes present: [{string.Join(", ", labels)}]");

            if (accessorName == "idx")
                return new Value(probes.RealValues[pi]);
            return new Value(ds[$"{accessorName}:{label}"]);
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

    /// <summary>
    /// <c>A.P("probe")</c> reads <c>Pprobe_fund_W</c>, <c>A.P("probe", k)</c> reads <c>Pprobe_W</c> at spectral index or
    /// product tag <c>k</c>, <c>A.Pt("probe")</c> reads <c>Pprobe_t_W</c> — the cubes the envelope and HB engines emit
    /// for every IProbe. The branch and spectral axes are found BY NAME and every other axis passes through, so the
    /// same text gives a scalar, a [Pin] curve or a [Vdd, Pin] surface as sweeps are wrapped around the analysis.
    /// There are deliberately no positional sweep arguments: they are the one thing that changes meaning when a sweep
    /// is added. One sweep point is <c>at(A.P("probe"), "Pin", i)</c>, by its position along that axis.
    /// </summary>
    private Value EvalProbePower(CallExpr cl, Scope scope, DataSet ds, string analysisName, string accessor, string probe)
    {
        if (accessor == "Pt" && cl.Args.Length != 1) throw new ArityException(cl.Name, 1, cl.Args.Length);
        string cubeName = accessor == "Pt" ? "Pprobe_t_W" : cl.Args.Length == 2 ? "Pprobe_W" : "Pprobe_fund_W";

        if (!ds.Contains("Pprobe_fund_W"))
            throw new ExpressionException(ds.Contains("__ProbeBranches")
                ? $"{cl.Name}: analysis '{analysisName}' carries no probe power; {accessor} is read on envelope and " +
                  "harmonic-balance analyses."
                : $"{cl.Name}: analysis '{analysisName}' has no IProbe. {accessor} reads the power through an IProbe: " +
                  "place one in series with the load (and one in each supply feed).");
        if (!ds.Contains(cubeName))
            throw new ExpressionException(
                $"{cl.Name}: the instantaneous power needs the waveforms; set SaveWaveforms=yes on '{analysisName}'.");

        var cube = ds[cubeName];
        int branchAxis = 0;
        while (cube.Axes[branchAxis].Name != "branch") branchAxis++;
        var labels = cube.Axes[branchAxis].Labels ?? [];
        int idx = Array.FindIndex(labels, s => s.Equals(probe, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new ExpressionException(
                $"{cl.Name}: no IProbe named '{probe}' in '{analysisName}'. IProbes: [{string.Join(", ", labels)}]");

        var sliceArgs = new object[cube.Rank];
        for (int a = 0; a < cube.Rank; a++)
            sliceArgs[a] = a == branchAxis ? idx
                : cube.Axes[a].Name is "harmonic" or "mixIndex" && cl.Args.Length == 2
                    ? ResolveSpectralArg(EvalExpr(cl.Args[1], scope), cube.Axes[a])
                    : Range.All;
        return SliceToValue(cube[sliceArgs]);
    }

    /// <summary>The 0-based index on a wsp row/col axis whose VALUE is the document's 1-based
    /// number, or an error naming the legal range.</summary>
    private static int WspAxisIndex(Axis ax, int value, string exprName, string which)
    {
        for (int k = 0; k < ax.Values.Length; k++)
            if ((int)Math.Round(ax.Values[k]) == value) return k;
        throw new ExpressionException(
            $"{exprName}: wsp {which} {value} is outside 1..{ax.Values.Length} " +
            $"(2 × the number of probes).");
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
                    if (axis.Name is "i" or "j") throw BracketPortRange(ix, axis);
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
            // The WSProbe matrix's row/col are the same: WspCubePacker writes 1-based axis values so
            // the document's wsp(r, c) is wsp[:, r, c] with no index arithmetic between.
            if (axis.Name is "i" or "j" or "row" or "col")
            {
                for (int k = 0; k < axis.Values.Length; k++)
                    if ((int)Math.Round(axis.Values[k]) == n) return k;
                string what = axis.Name is "row" or "col" ? "Index" : "Port";
                throw new ExpressionException(
                    $"{what} {n} not found on axis '{axis.Name}'. Available: " +
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

    /// <summary><c>pow(a, b)</c>: over a cube it is <c>a^b</c>, element-wise; on two numbers it keeps
    /// its own scalar path.</summary>
    private Value PowCall(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
        var a = EvalExpr(cl.Args[0], scope);
        var b = EvalExpr(cl.Args[1], scope);
        if (a.Kind == ValueKind.Cube || b.Kind == ValueKind.Cube) return Value.Pow(a, b);
        var kind = Value.CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(Math.Pow(a.AsReal(), b.AsReal()))
            : new Value(Complex.Pow(a.ToComplex(), b.ToComplex()));
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
        // abs is mag (expressions.md): a result cube takes the same element-wise magnitude.
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Mag());
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

    /// <summary>
    /// <c>sqrt</c>, cube-aware — unlike the rest of the <see cref="UnaryMath"/> family, which takes a
    /// scalar only.
    ///
    /// <para><b>It is cube-aware because there is no other way to write one.</b> A per-frequency
    /// square root has no spelling otherwise: <c>pow</c> and <c>^</c> both route through
    /// <see cref="Value.Pow"/>, which is scalar too, and <c>exp(0.5*log(x))</c> fails on <c>exp</c>
    /// for the same reason. A coupling factor k = M/√(L₁L₂) read out of a swept two-port is the case
    /// that found it (the shipped CoupledInductors example) and it is not an unusual shape — an RMS,
    /// a |Z| taken from Z², a geometric mean of two swept quantities all want the same thing.</para>
    ///
    /// <para>The Real/Complex branches are <see cref="SafeSqrt"/> and <see cref="Complex.Sqrt"/>
    /// exactly as before, so a scalar argument behaves identically; only the Cube case is new.</para>
    /// </summary>
    private Value EvalSqrt(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("sqrt", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Sqrt());
        return v.Kind == ValueKind.Real
            ? new Value(SafeSqrt(v.AsReal()))
            : new Value(Complex.Sqrt(v.AsComplex()));
    }

    private Value EvalImag(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException("imag", 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind == ValueKind.Cube) return new Value(v.AsCube().Imag());
        return v.Kind == ValueKind.Real ? new Value(0.0) : new Value(v.AsComplex().Imaginary);
    }

    /// <summary>
    /// <c>at(x, "axis", index)</c> — pins one axis of a cube-valued expression BY NAME, keeping every
    /// other axis. The reason it exists: a positional pin (<c>HB1.V("Vout", 1, 0)</c> or
    /// <c>HB1.V[0, "Vout", 1]</c>) addresses a POSITION, so adding an outer parametric sweep silently
    /// moves it to a different axis — or, in the bracket form, breaks the expression outright. With
    /// <c>at</c>, an AM-PM referenced to the first drive point is ONE expression at any sweep depth:
    /// <code>
    ///   trans_phase = phase(HB1.V("Vout", 1))
    ///   AMPM        = trans_phase - at(trans_phase, "Pin", 0)
    /// </code>
    /// <c>[RFfreq, Pin] − [RFfreq]</c> broadcasts by axis name, so the reference stays PER-FREQUENCY
    /// rather than collapsing to one number for the whole run.
    /// <para>A negative index counts from the end (−1 = last point).</para>
    /// <para><b>Deliberately strict.</b> A value with no such axis is an error naming the axes there
    /// are, never a silent no-op: returning the value unchanged would make a mistyped axis name — or a
    /// sweep that is switched off — read as "AM-PM is identically zero", which is precisely the
    /// failure mode this function was added to remove.</para>
    /// </summary>
    private Value EvalAt(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 3) throw new ArityException("at", 3, cl.Args.Length);

        var v       = EvalExpr(cl.Args[0], scope);
        var axisVal = EvalExpr(cl.Args[1], scope);
        string axis = axisVal.Kind == ValueKind.String ? axisVal.AsString() : axisVal.ToString()!;

        var idxVal = EvalExpr(cl.Args[2], scope);
        if (idxVal.Kind != ValueKind.Real)
            throw new ExpressionException($"at(): the index must be a number, e.g. at(x, \"{axis}\", 0).");
        int index = (int)Math.Round(idxVal.AsReal());

        if (v.Kind != ValueKind.Cube)
            throw new ExpressionException(
                $"at(): this value is a single number, so it has no '{axis}' axis to pin " +
                "(the sweep may be switched off).");

        try
        {
            return new Value(v.AsCube().At(axis, index));
        }
        catch (ArgumentException ex)          // unknown axis name, or index out of range
        {
            throw new ExpressionException($"at(): {ex.Message}");
        }
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

    /// <summary>
    /// <c>complex(re, im)</c> — the rectangular counterpart of <see cref="EvalPolar"/>. Both parts
    /// must be Real: a complex "real part" is not a rounding error to absorb, it means the
    /// expression that produced it is wrong.
    /// </summary>
    private Value EvalComplex(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException("complex", 2, cl.Args.Length);
        var re = EvalExpr(cl.Args[0], scope);
        var im = EvalExpr(cl.Args[1], scope);
        if (re.Kind != ValueKind.Real) throw new TypeErrorException("complex() real part must be Real");
        if (im.Kind != ValueKind.Real) throw new TypeErrorException("complex() imaginary part must be Real");
        return new Value(new Complex(re.AsReal(), im.AsReal()));
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
