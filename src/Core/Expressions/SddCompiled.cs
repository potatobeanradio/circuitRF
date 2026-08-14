namespace CircuitRF.Core.Expressions;

// ── §1.3 step 1 (brief-harmonicarf-r3b) ─────────────────────────────────────────────────────────
//
// SddEvaluator.Eval<T> walks the raw Expr tree against a freshly-built Dictionary<string,T> on
// EVERY call — a per-call allocation, a per-call "_v{i+1}" string interpolation, and a string-hashed
// dictionary lookup at every RefExpr. That dictionary and SddEvaluator.EvalDual/EvalDouble are left
// completely UNTOUCHED (they remain the reference implementation the bit-identical gate compares
// against, and the API every other caller still uses directly).
//
// CompiledSddExpr is the fast path SddModel uses instead: RefExpr is resolved to an array SLOT
// index ONCE, when the AST is compiled (SddModel does this once per equation, in its constructor,
// exactly where the ASTs are already cached). Evaluation then touches no Dictionary, hashes no
// string, and interpolates nothing — it is a switch over a small compiled node tree indexed by
// integer slot. Every arithmetic op below calls the exact same static T.Add/Sub/.../Exp/... methods
// SddEvaluator.Eval<T> calls, in the exact same order, so results are bit-identical by construction.

// A plain `node switch { CConstNode c => .., CSlotNode s => .., ... }` type-pattern match compiles to
// a CHAIN of `isinst` checks in declaration order — measured to cost more per node than the
// dictionary lookup it replaces once the tree is large (the shipped default's 80-node drain equation
// got SLOWER, not faster, on the first cut of this file). An explicit integer discriminant lets the
// switch below become a single jump-table branch instead, which is what actually pays off on a big
// tree; see EvaluatorPerfCostTests for the before/after.
internal enum NKind { Const, Slot, Error, Neg, Bin, Fn1, Sinh, Cosh, Tan, Log10, MinMax, Sign, MathConst, Atan2, CondValue }

internal abstract class CNode(NKind kind) { public readonly NKind Kind = kind; }

internal sealed class CConstNode(double Value) : CNode(NKind.Const) { public readonly double Value = Value; }
internal sealed class CSlotNode(int Slot) : CNode(NKind.Slot) { public readonly int Slot = Slot; }
internal sealed class CErrorNode(Func<Exception> Make) : CNode(NKind.Error) { public readonly Func<Exception> Make = Make; }
internal sealed class CNegNode(CNode Operand) : CNode(NKind.Neg) { public readonly CNode Operand = Operand; }

internal enum CBinOp { Add, Sub, Mul, Div, Pow }
internal sealed class CBinNode(CBinOp Op, CNode Left, CNode Right) : CNode(NKind.Bin)
{
    public readonly CBinOp Op = Op;
    public readonly CNode Left = Left, Right = Right;
}

internal enum CFn1 { Exp, Log, Sqrt, Tanh, Sin, Cos, Abs }
internal sealed class CFn1Node(CFn1 Fn, CNode Arg) : CNode(NKind.Fn1)
{
    public readonly CFn1 Fn = Fn;
    public readonly CNode Arg = Arg;
}

// sinh/cosh/tan/log10 are composite in the reference implementation. Each is its own node (rather
// than generic composition) so the shared sub-expression is evaluated exactly as many times as the
// reference evaluates it — once for sinh/cosh/log10 (a local `var x = Arg(0)` reused), TWICE for tan
// (the reference calls `Arg(0)` twice, independently) — matching side effects (domain-warning
// console output) as well as the value.
internal sealed class CSinhNode(CNode Arg) : CNode(NKind.Sinh) { public readonly CNode Arg = Arg; }
internal sealed class CCoshNode(CNode Arg) : CNode(NKind.Cosh) { public readonly CNode Arg = Arg; }
internal sealed class CTanNode(CNode Arg) : CNode(NKind.Tan) { public readonly CNode Arg = Arg; }
internal sealed class CLog10Node(CNode Arg) : CNode(NKind.Log10) { public readonly CNode Arg = Arg; }

internal sealed class CMinMaxNode(bool IsMax, CNode A, CNode B) : CNode(NKind.MinMax)
{
    public readonly bool IsMax = IsMax;
    public readonly CNode A = A, B = B;
}
internal sealed class CSignNode(CNode Arg) : CNode(NKind.Sign) { public readonly CNode Arg = Arg; }
internal sealed class CMathConstNode(Func<double, double> Fn, CNode Arg) : CNode(NKind.MathConst)
{
    public readonly Func<double, double> Fn = Fn;
    public readonly CNode Arg = Arg;
}
internal sealed class CAtan2Node(CNode A, CNode B) : CNode(NKind.Atan2) { public readonly CNode A = A, B = B; }

internal sealed class CCondValueNode(CCond Cond, CNode Then, CNode Else) : CNode(NKind.CondValue)
{
    public readonly CCond Cond = Cond;
    public readonly CNode Then = Then, Else = Else;
}

// ── the condition sub-tree (bool-valued), mirroring EvalConditionAsBool ─────────────────────────

internal abstract class CCond;

internal sealed class CCompareNode(string Op, CNode Left, CNode Right) : CCond
{
    public readonly string Op = Op;
    public readonly CNode Left = Left, Right = Right;
}
internal sealed class CLogicNode(string Op, CCond Left, CCond Right) : CCond
{
    public readonly string Op = Op;
    public readonly CCond Left = Left, Right = Right;
}
internal sealed class CNotNode(CCond Inner) : CCond { public readonly CCond Inner = Inner; }
internal sealed class CCondTernaryNode(CCond Cond, CCond Then, CCond Else) : CCond
{
    public readonly CCond Cond = Cond, Then = Then, Else = Else;
}
internal sealed class CCondErrorNode(Func<Exception> Make) : CCond { public readonly Func<Exception> Make = Make; }

/// <summary>Compiles an <see cref="Expr"/> AST into a <see cref="CNode"/> tree against a fixed name
/// → slot map, and evaluates a compiled tree generically over any <see cref="IAdScalar{T}"/>.
/// A name that fails to resolve, or a construct that is always a type/arity error, compiles to an
/// error node that throws the SAME exception the reference evaluator would — but LAZILY, only if
/// actually reached at evaluation time, exactly preserving the reference's short-circuit behaviour
/// (an unresolved name or bad arity inside an untaken conditional branch must not throw).</summary>
internal static class SddCompiler
{
    public static CNode CompileNode(Expr e, IReadOnlyDictionary<string, int> slotOf, string modelName) => e switch
    {
        NumberExpr n       => new CConstNode(n.Value),
        ConstExpr c        => CompileConst(c.Name),
        RefExpr r          => slotOf.TryGetValue(r.Name, out int slot)
            ? new CSlotNode(slot)
            : new CErrorNode(() => new UnresolvedNameException(r.Name, modelName)),
        UnaryExpr u        => CompileUnary(u, slotOf, modelName),
        BinaryExpr b       => CompileBinary(b, slotOf, modelName),
        CompareExpr        => new CErrorNode(() => new TypeErrorException(
                                  "SDD context: comparison result is Bool, cannot use as scalar")),
        LogicExpr          => new CErrorNode(() => new TypeErrorException(
                                  "SDD context: logical result is Bool, cannot use as scalar")),
        ConditionalExpr cd => new CCondValueNode(CompileCond(cd.Condition, slotOf, modelName),
                                                  CompileNode(cd.Then, slotOf, modelName),
                                                  CompileNode(cd.Else, slotOf, modelName)),
        CallExpr cl        => CompileCall(cl, slotOf, modelName),
        StringLiteralExpr  => new CErrorNode(() => new TypeErrorException(
                                  "SDD context: string literal not allowed in expression")),
        _                  => new CErrorNode(() => new ExpressionException($"Unknown AST node: {e.GetType().Name}")),
    };

    private static CNode CompileConst(string name) => name switch
    {
        "pi" => new CConstNode(Math.PI),
        "e"  => new CConstNode(Math.E),
        "j"  => new CErrorNode(() => new TypeErrorException(
                    "SDD context: imaginary unit 'j' not allowed (SDD equations are real-only)")),
        _    => new CErrorNode(() => new ExpressionException($"Unknown constant '{name}'")),
    };

    private static CNode CompileUnary(UnaryExpr u, IReadOnlyDictionary<string, int> slotOf, string modelName) => u.Op switch
    {
        "-" => new CNegNode(CompileNode(u.Operand, slotOf, modelName)),
        "+" => CompileNode(u.Operand, slotOf, modelName),
        "!" => new CErrorNode(() => new TypeErrorException("SDD context: '!' not allowed (SDD equations are real-only)")),
        _   => new CErrorNode(() => new ExpressionException($"Unknown unary op '{u.Op}'")),
    };

    private static CNode CompileBinary(BinaryExpr b, IReadOnlyDictionary<string, int> slotOf, string modelName)
    {
        var l = CompileNode(b.Left, slotOf, modelName);
        var r = CompileNode(b.Right, slotOf, modelName);
        return b.Op switch
        {
            "+" => new CBinNode(CBinOp.Add, l, r),
            "-" => new CBinNode(CBinOp.Sub, l, r),
            "*" => new CBinNode(CBinOp.Mul, l, r),
            "/" => new CBinNode(CBinOp.Div, l, r),
            "^" => new CBinNode(CBinOp.Pow, l, r),
            _   => new CErrorNode(() => new ExpressionException($"Unknown binary op '{b.Op}'")),
        };
    }

    private static CNode CompileCall(CallExpr cl, IReadOnlyDictionary<string, int> slotOf, string modelName)
    {
        CNode N(Expr e) => CompileNode(e, slotOf, modelName);
        CNode Arg(int i, int expected) => cl.Args.Length > i
            ? N(cl.Args[i])
            : new CErrorNode(() => new ArityException(cl.Name, expected, cl.Args.Length));
        CNode ArityError(int expected) => new CErrorNode(() => new ArityException(cl.Name, expected, cl.Args.Length));
        bool Arity(int expected) => cl.Args.Length == expected;

        switch (cl.Name)
        {
            case "exp":   return Arity(1) ? new CFn1Node(CFn1.Exp,  Arg(0, 1)) : ArityError(1);
            case "log":
            case "ln":    return Arity(1) ? new CFn1Node(CFn1.Log,  Arg(0, 1)) : ArityError(1);
            case "sqrt":  return Arity(1) ? new CFn1Node(CFn1.Sqrt, Arg(0, 1)) : ArityError(1);
            case "tanh":  return Arity(1) ? new CFn1Node(CFn1.Tanh, Arg(0, 1)) : ArityError(1);
            case "sin":   return Arity(1) ? new CFn1Node(CFn1.Sin,  Arg(0, 1)) : ArityError(1);
            case "cos":   return Arity(1) ? new CFn1Node(CFn1.Cos,  Arg(0, 1)) : ArityError(1);
            case "abs":   return Arity(1) ? new CFn1Node(CFn1.Abs,  Arg(0, 1)) : ArityError(1);
            case "pow":   return Arity(2) ? new CBinNode(CBinOp.Pow, Arg(0, 2), Arg(1, 2)) : ArityError(2);
            case "sinh":  return Arity(1) ? new CSinhNode(Arg(0, 1)) : ArityError(1);
            case "cosh":  return Arity(1) ? new CCoshNode(Arg(0, 1)) : ArityError(1);
            case "tan":   return Arity(1) ? new CTanNode(Arg(0, 1)) : ArityError(1);
            case "log10": return Arity(1) ? new CLog10Node(Arg(0, 1)) : ArityError(1);
            case "min":   return Arity(2) ? new CMinMaxNode(false, Arg(0, 2), Arg(1, 2)) : ArityError(2);
            case "max":   return Arity(2) ? new CMinMaxNode(true,  Arg(0, 2), Arg(1, 2)) : ArityError(2);
            case "sign":  return Arity(1) ? new CSignNode(Arg(0, 1)) : ArityError(1);
            case "if":
                return cl.Args.Length == 3
                    ? new CCondValueNode(CompileCond(cl.Args[0], slotOf, modelName), N(cl.Args[1]), N(cl.Args[2]))
                    : new CErrorNode(() => new ArityException("if", 3, cl.Args.Length));
            case "atan":  return Arity(1) ? new CMathConstNode(Math.Atan, Arg(0, 1)) : ArityError(1);
            case "atan2": return Arity(2) ? new CAtan2Node(Arg(0, 2), Arg(1, 2)) : ArityError(2);
            case "asin":  return Arity(1) ? new CMathConstNode(Math.Asin, Arg(0, 1)) : ArityError(1);
            case "acos":  return Arity(1) ? new CMathConstNode(Math.Acos, Arg(0, 1)) : ArityError(1);
            default:      return new CErrorNode(() => new UnknownFunctionException(cl.Name));
        }
    }

    private static CCond CompileCond(Expr cond, IReadOnlyDictionary<string, int> slotOf, string modelName) => cond switch
    {
        CompareExpr cmp => new CCompareNode(cmp.Op, CompileNode(cmp.Left, slotOf, modelName), CompileNode(cmp.Right, slotOf, modelName)),
        LogicExpr lg    => new CLogicNode(lg.Op, CompileCond(lg.Left, slotOf, modelName), CompileCond(lg.Right, slotOf, modelName)),
        UnaryExpr { Op: "!" } u => new CNotNode(CompileCond(u.Operand, slotOf, modelName)),
        ConditionalExpr cd => new CCondTernaryNode(
            CompileCond(cd.Condition, slotOf, modelName), CompileCond(cd.Then, slotOf, modelName), CompileCond(cd.Else, slotOf, modelName)),
        _ => new CCondErrorNode(() => new TypeErrorException(
                $"SDD conditional: expected a comparison or logical expression, got {cond.GetType().Name}")),
    };

    // ── evaluation ───────────────────────────────────────────────────────────────────────────────

    public static T Eval<T>(CNode node, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        switch (node.Kind)
        {
            case NKind.Const: return T.Constant(((CConstNode)node).Value);
            case NKind.Slot:  return slots[((CSlotNode)node).Slot];
            case NKind.Error: throw ((CErrorNode)node).Make();
            case NKind.Neg:   return T.Neg(Eval(((CNegNode)node).Operand, slots));
            case NKind.Bin:   return EvalBin((CBinNode)node, slots);
            case NKind.Fn1:   return EvalFn1((CFn1Node)node, slots);
            case NKind.Sinh:  return EvalSinh((CSinhNode)node, slots);
            case NKind.Cosh:  return EvalCosh((CCoshNode)node, slots);
            case NKind.Tan:
            {
                var tn = (CTanNode)node;
                return T.Div(T.Sin(Eval(tn.Arg, slots)), T.Cos(Eval(tn.Arg, slots)));
            }
            case NKind.Log10:
            {
                var lg = (CLog10Node)node;
                return T.Div(T.Log(Eval(lg.Arg, slots)), T.Constant(Math.Log(10.0)));
            }
            case NKind.MinMax: return EvalMinMax((CMinMaxNode)node, slots);
            case NKind.Sign:   return T.Constant(Math.Sign(T.ValueOf(Eval(((CSignNode)node).Arg, slots))));
            case NKind.MathConst:
            {
                var mc = (CMathConstNode)node;
                return T.Constant(mc.Fn(T.ValueOf(Eval(mc.Arg, slots))));
            }
            case NKind.Atan2:
            {
                var a2 = (CAtan2Node)node;
                return T.Constant(Math.Atan2(T.ValueOf(Eval(a2.A, slots)), T.ValueOf(Eval(a2.B, slots))));
            }
            case NKind.CondValue:
            {
                var cv = (CCondValueNode)node;
                return EvalCondBool(cv.Cond, slots) ? Eval(cv.Then, slots) : Eval(cv.Else, slots);
            }
            default: throw new ExpressionException($"Unknown compiled node kind: {node.Kind}");
        }
    }

    private static T EvalBin<T>(CBinNode b, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        var l = Eval(b.Left, slots);
        var r = Eval(b.Right, slots);
        return b.Op switch
        {
            CBinOp.Add => T.Add(l, r),
            CBinOp.Sub => T.Sub(l, r),
            CBinOp.Mul => T.Mul(l, r),
            CBinOp.Div => T.Div(l, r),
            CBinOp.Pow => T.Pow(l, r),
            _          => throw new ExpressionException("unreachable binary op"),
        };
    }

    private static T EvalFn1<T>(CFn1Node f, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        var a = Eval(f.Arg, slots);
        return f.Fn switch
        {
            CFn1.Exp  => T.Exp(a),
            CFn1.Log  => T.Log(a),
            CFn1.Sqrt => T.Sqrt(a),
            CFn1.Tanh => T.Tanh(a),
            CFn1.Sin  => T.Sin(a),
            CFn1.Cos  => T.Cos(a),
            CFn1.Abs  => T.Abs(a),
            _         => throw new ExpressionException("unreachable function"),
        };
    }

    // x captured ONCE and reused twice — matches the reference's `var x = Arg(0);` for sinh/cosh.
    private static T EvalSinh<T>(CSinhNode sh, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        var x = Eval(sh.Arg, slots);
        return T.Div(T.Sub(T.Exp(x), T.Exp(T.Neg(x))), T.Constant(2.0));
    }

    private static T EvalCosh<T>(CCoshNode ch, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        var x = Eval(ch.Arg, slots);
        return T.Div(T.Add(T.Exp(x), T.Exp(T.Neg(x))), T.Constant(2.0));
    }

    private static T EvalMinMax<T>(CMinMaxNode mm, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        var a = Eval(mm.A, slots);
        var b = Eval(mm.B, slots);
        return mm.IsMax
            ? (T.ValueOf(a) >= T.ValueOf(b) ? a : b)
            : (T.ValueOf(a) <= T.ValueOf(b) ? a : b);
    }

    public static bool EvalCondBool<T>(CCond c, ReadOnlySpan<T> slots) where T : IAdScalar<T>
    {
        switch (c)
        {
            case CCompareNode cmp:
            {
                double l = T.ValueOf(Eval(cmp.Left, slots));
                double r = T.ValueOf(Eval(cmp.Right, slots));
                return cmp.Op switch
                {
                    "<"  => l < r,
                    "<=" => l <= r,
                    ">"  => l > r,
                    ">=" => l >= r,
                    "==" => l == r,
                    "!=" => l != r,
                    _    => throw new ExpressionException($"Unknown comparison op '{cmp.Op}'"),
                };
            }
            case CLogicNode lg:
            {
                bool l = EvalCondBool(lg.Left, slots);
                if (lg.Op == "&&" && !l) return false;
                if (lg.Op == "||" && l) return true;
                bool r = EvalCondBool(lg.Right, slots);
                return lg.Op == "&&" ? l && r : l || r;
            }
            case CNotNode nt: return !EvalCondBool(nt.Inner, slots);
            case CCondTernaryNode ct: return EvalCondBool(ct.Cond, slots) ? EvalCondBool(ct.Then, slots) : EvalCondBool(ct.Else, slots);
            case CCondErrorNode err: throw err.Make();
            default: throw new ExpressionException($"Unknown compiled condition node: {c.GetType().Name}");
        }
    }
}
