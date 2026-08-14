namespace CircuitRF.Core.Expressions;

// ── §1.3 step 3 (brief-harmonicarf-r3b) ─────────────────────────────────────────────────────────
//
// Step 1's CompiledSddExpr/SddCompiler killed the per-call dictionary but is still a WALK OF BOXED
// NODES — measured to cost roughly as much per node as the reference it replaced (the per-node cost
// is dispatch + struct traffic inherent to a tree-of-objects shape, not lookup cost). This file is
// the actual fix: at COMPILE time (once, in CompiledSddExpr's constructor), the AST is flattened into
// a linear array of three-address instructions — no recursion, no boxed node objects, one register
// per instruction, evaluated by a single tight loop with a switch on a byte opcode. Every instruction
// calls the exact same static Dual.Add/Sub/.../Exp/... methods the reference and step 1 call, in the
// same order, so results stay bit-identical.
//
// SCOPE: this covers every equation that contains no conditional (`if`/ternary) — which is every real
// SDD equation in this repository today (shipped default, every testdata/ fixture). An equation that
// DOES contain one falls back to step 1's CNode tree-walk (SddCompiler.Eval), which is still correct
// and still faster than the pre-step-1 dictionary path — a hand-rolled bytecode VM's jump/branch
// correctness is not worth taking on for a construct with zero measured real-world coverage; see
// CompiledSddExpr's own remarks.

internal enum ROp : byte
{
    Const, Neg, Add, Sub, Mul, Div, Pow,
    Exp, Log, Sqrt, Tanh, Sin, Cos, Abs,
    ConstOfValue,           // unary; Extra indexes a Func<double,double> table (atan/asin/acos)
    Sign,                   // unary
    Atan2,                  // binary
    SelectMin, SelectMax,   // binary — copies the CHOSEN operand's Dual verbatim (value AND gradient)
    Throw,                  // 0-ary; Extra indexes a Func<Exception> table
}

internal readonly struct RInstr(ROp op, int a = 0, int b = 0, double lit = 0.0, int extra = 0)
{
    public readonly ROp Op = op;
    public readonly int A = a, B = b;
    public readonly double Lit = lit;
    public readonly int Extra = extra;
}

/// <summary>
/// The flattening compiler. Mirrors <see cref="SddCompiler"/>'s CompileNode switch exactly, node for
/// node, but EMITS an instruction and returns the ABSOLUTE register index holding the result, instead
/// of building a <see cref="CNode"/>. Register space is one flat array: indices
/// <c>[0, totalSlots)</c> are the input slots (ports/controls/params, same layout
/// <see cref="CompiledSddExpr"/> already uses); index <c>totalSlots + i</c> is instruction <c>i</c>'s
/// own output, in program order. A bare name/leaf reference needs no instruction at all — it returns
/// its slot index directly, which is why the code list can be SHORTER than the node count of the
/// equivalent CNode tree.
/// </summary>
internal static class SddRegisterCompiler
{
    /// <summary>True if <paramref name="e"/> contains a conditional anywhere (a plain
    /// <c>ConditionalExpr</c> or an <c>if(...)</c> call) — the one construct this compiler does not
    /// handle. Checked once, before compiling, so the caller can fall back cleanly.</summary>
    public static bool ContainsConditional(Expr e) => e switch
    {
        ConditionalExpr => true,
        CallExpr { Name: "if" } => true,
        UnaryExpr u => ContainsConditional(u.Operand),
        BinaryExpr b => ContainsConditional(b.Left) || ContainsConditional(b.Right),
        CallExpr cl => cl.Args.Any(ContainsConditional),
        _ => false,
    };

    /// <summary>Compiles <paramref name="ast"/> (known, by the caller, to contain no conditional)
    /// into a flat instruction list. Returns the instructions, the math-function side table
    /// (for <see cref="ROp.ConstOfValue"/>), the exception side table (for <see cref="ROp.Throw"/>),
    /// and the ABSOLUTE register index holding the whole expression's value.</summary>
    public static (RInstr[] Code, Func<double, double>[] MathFns, Func<Exception>[] ExFns, int RootReg) Compile(
        Expr ast, IReadOnlyDictionary<string, int> slotOf, int totalSlots, string modelName)
    {
        var code = new List<RInstr>();
        var mathFns = new List<Func<double, double>>();
        var exFns = new List<Func<Exception>>();

        int EmitThrow(Func<Exception> make)
        {
            exFns.Add(make);
            code.Add(new RInstr(ROp.Throw, extra: exFns.Count - 1));
            return totalSlots + code.Count - 1;
        }

        int Reg() => totalSlots + code.Count - 1;

        int Emit(Expr e)
        {
            switch (e)
            {
                case NumberExpr n:
                    code.Add(new RInstr(ROp.Const, lit: n.Value));
                    return Reg();

                case ConstExpr c:
                    return c.Name switch
                    {
                        "pi" => EmitConst(Math.PI),
                        "e"  => EmitConst(Math.E),
                        "j"  => EmitThrow(() => new TypeErrorException(
                                    "SDD context: imaginary unit 'j' not allowed (SDD equations are real-only)")),
                        _    => EmitThrow(() => new ExpressionException($"Unknown constant '{c.Name}'")),
                    };

                case RefExpr r:
                    return slotOf.TryGetValue(r.Name, out int slot)
                        ? slot
                        : EmitThrow(() => new UnresolvedNameException(r.Name, modelName));

                case UnaryExpr u:
                    return u.Op switch
                    {
                        "-" => EmitUnary(ROp.Neg, u.Operand),
                        "+" => Emit(u.Operand),
                        "!" => EmitThrow(() => new TypeErrorException("SDD context: '!' not allowed (SDD equations are real-only)")),
                        _   => EmitThrow(() => new ExpressionException($"Unknown unary op '{u.Op}'")),
                    };

                case BinaryExpr b:
                {
                    // The reference always evaluates BOTH operands before checking the operator —
                    // mirrored: emit l then r unconditionally, regardless of whether Op is valid.
                    int l = Emit(b.Left);
                    int r = Emit(b.Right);
                    ROp? op = b.Op switch
                    {
                        "+" => ROp.Add, "-" => ROp.Sub, "*" => ROp.Mul, "/" => ROp.Div, "^" => ROp.Pow, _ => null,
                    };
                    if (op is { } o) { code.Add(new RInstr(o, l, r)); return Reg(); }
                    return EmitThrow(() => new ExpressionException($"Unknown binary op '{b.Op}'"));
                }

                case CompareExpr:
                    return EmitThrow(() => new TypeErrorException("SDD context: comparison result is Bool, cannot use as scalar"));
                case LogicExpr:
                    return EmitThrow(() => new TypeErrorException("SDD context: logical result is Bool, cannot use as scalar"));

                case ConditionalExpr:
                    // Unreachable: the caller pre-scans with ContainsConditional and never compiles
                    // a tree containing one through this path.
                    throw new InvalidOperationException("SddRegisterCompiler: conditional reached the register path");

                case CallExpr cl:
                    return EmitCall(cl);

                case StringLiteralExpr:
                    return EmitThrow(() => new TypeErrorException("SDD context: string literal not allowed in expression"));

                default:
                    return EmitThrow(() => new ExpressionException($"Unknown AST node: {e.GetType().Name}"));
            }
        }

        int EmitConst(double v) { code.Add(new RInstr(ROp.Const, lit: v)); return Reg(); }
        int EmitUnary(ROp op, Expr operand) { int a = Emit(operand); code.Add(new RInstr(op, a)); return Reg(); }
        int EmitOp1(ROp op, int argReg) { code.Add(new RInstr(op, argReg)); return Reg(); }

        int EmitMathConst(Func<double, double> fn, Expr arg)
        {
            int a = Emit(arg);
            mathFns.Add(fn);
            code.Add(new RInstr(ROp.ConstOfValue, a, extra: mathFns.Count - 1));
            return Reg();
        }

        int EmitCall(CallExpr cl)
        {
            int ArgOrThrow(int i, int expected) => cl.Args.Length > i
                ? Emit(cl.Args[i])
                : EmitThrow(() => new ArityException(cl.Name, expected, cl.Args.Length));
            int ArityThrow(int expected) => EmitThrow(() => new ArityException(cl.Name, expected, cl.Args.Length));
            bool Arity(int expected) => cl.Args.Length == expected;

            switch (cl.Name)
            {
                case "exp":   return Arity(1) ? EmitOp1(ROp.Exp,  ArgOrThrow(0, 1)) : ArityThrow(1);
                case "log":
                case "ln":    return Arity(1) ? EmitOp1(ROp.Log,  ArgOrThrow(0, 1)) : ArityThrow(1);
                case "sqrt":  return Arity(1) ? EmitOp1(ROp.Sqrt, ArgOrThrow(0, 1)) : ArityThrow(1);
                case "tanh":  return Arity(1) ? EmitOp1(ROp.Tanh, ArgOrThrow(0, 1)) : ArityThrow(1);
                case "sin":   return Arity(1) ? EmitOp1(ROp.Sin,  ArgOrThrow(0, 1)) : ArityThrow(1);
                case "cos":   return Arity(1) ? EmitOp1(ROp.Cos,  ArgOrThrow(0, 1)) : ArityThrow(1);
                case "abs":   return Arity(1) ? EmitOp1(ROp.Abs,  ArgOrThrow(0, 1)) : ArityThrow(1);
                case "pow":
                {
                    if (!Arity(2)) return ArityThrow(2);
                    int a = Emit(cl.Args[0]), b = Emit(cl.Args[1]);
                    code.Add(new RInstr(ROp.Pow, a, b));
                    return Reg();
                }
                case "sinh":
                {
                    if (!Arity(1)) return ArityThrow(1);
                    // x captured once, reused twice — matches the reference's `var x = Arg(0)`.
                    int x = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Exp, x)); int ex = Reg();
                    code.Add(new RInstr(ROp.Neg, x)); int nx = Reg();
                    code.Add(new RInstr(ROp.Exp, nx)); int enx = Reg();
                    code.Add(new RInstr(ROp.Sub, ex, enx)); int diff = Reg();
                    code.Add(new RInstr(ROp.Const, lit: 2.0)); int two = Reg();
                    code.Add(new RInstr(ROp.Div, diff, two));
                    return Reg();
                }
                case "cosh":
                {
                    if (!Arity(1)) return ArityThrow(1);
                    int x = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Exp, x)); int ex = Reg();
                    code.Add(new RInstr(ROp.Neg, x)); int nx = Reg();
                    code.Add(new RInstr(ROp.Exp, nx)); int enx = Reg();
                    code.Add(new RInstr(ROp.Add, ex, enx)); int sum = Reg();
                    code.Add(new RInstr(ROp.Const, lit: 2.0)); int two = Reg();
                    code.Add(new RInstr(ROp.Div, sum, two));
                    return Reg();
                }
                case "tan":
                {
                    if (!Arity(1)) return ArityThrow(1);
                    // The reference calls Arg(0) TWICE, independently — reproduced here by emitting
                    // the argument's subtree twice rather than sharing one register, so a domain
                    // warning inside it (if any) fires the same number of times.
                    int x1 = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Sin, x1)); int s = Reg();
                    int x2 = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Cos, x2)); int c = Reg();
                    code.Add(new RInstr(ROp.Div, s, c));
                    return Reg();
                }
                case "log10":
                {
                    if (!Arity(1)) return ArityThrow(1);
                    int x = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Log, x)); int lg = Reg();
                    code.Add(new RInstr(ROp.Const, lit: Math.Log(10.0))); int ln10 = Reg();
                    code.Add(new RInstr(ROp.Div, lg, ln10));
                    return Reg();
                }
                case "min":
                {
                    if (!Arity(2)) return ArityThrow(2);
                    int a = Emit(cl.Args[0]), b = Emit(cl.Args[1]);
                    code.Add(new RInstr(ROp.SelectMin, a, b));
                    return Reg();
                }
                case "max":
                {
                    if (!Arity(2)) return ArityThrow(2);
                    int a = Emit(cl.Args[0]), b = Emit(cl.Args[1]);
                    code.Add(new RInstr(ROp.SelectMax, a, b));
                    return Reg();
                }
                case "sign":
                {
                    if (!Arity(1)) return ArityThrow(1);
                    int a = Emit(cl.Args[0]);
                    code.Add(new RInstr(ROp.Sign, a));
                    return Reg();
                }
                case "if":
                    // Unreachable — ContainsConditional catches this before compilation starts.
                    throw new InvalidOperationException("SddRegisterCompiler: 'if' reached the register path");
                case "atan":  return Arity(1) ? EmitMathConst(Math.Atan, cl.Args[0]) : ArityThrow(1);
                case "atan2":
                {
                    if (!Arity(2)) return ArityThrow(2);
                    int a = Emit(cl.Args[0]), b = Emit(cl.Args[1]);
                    code.Add(new RInstr(ROp.Atan2, a, b));
                    return Reg();
                }
                case "asin":  return Arity(1) ? EmitMathConst(Math.Asin, cl.Args[0]) : ArityThrow(1);
                case "acos":  return Arity(1) ? EmitMathConst(Math.Acos, cl.Args[0]) : ArityThrow(1);
                default:      return EmitThrow(() => new UnknownFunctionException(cl.Name));
            }
        }

        int root = Emit(ast);
        return ([.. code], [.. mathFns], [.. exFns], root);
    }

    /// <summary>Runs a compiled register program in Dual arithmetic. <paramref name="v"/> must be
    /// sized <c>totalSlots + code.Length</c>, with <c>v[0..totalSlots)</c> already populated
    /// (port/control seeds + parameter constants) by the caller.</summary>
    public static Dual Run(RInstr[] code, Func<double, double>[] mathFns, Func<Exception>[] exFns,
                            int totalSlots, int rootReg, Span<Dual> v)
    {
        for (int pc = 0; pc < code.Length; pc++)
        {
            ref readonly var ins = ref code[pc];
            int o = totalSlots + pc;
            switch (ins.Op)
            {
                case ROp.Const: v[o] = Dual.Constant(ins.Lit); break;
                case ROp.Neg:   v[o] = Dual.Neg(v[ins.A]); break;
                case ROp.Add:   v[o] = Dual.Add(v[ins.A], v[ins.B]); break;
                case ROp.Sub:   v[o] = Dual.Sub(v[ins.A], v[ins.B]); break;
                case ROp.Mul:   v[o] = Dual.Mul(v[ins.A], v[ins.B]); break;
                case ROp.Div:   v[o] = Dual.Div(v[ins.A], v[ins.B]); break;
                case ROp.Pow:   v[o] = Dual.Pow(v[ins.A], v[ins.B]); break;
                case ROp.Exp:   v[o] = Dual.Exp(v[ins.A]); break;
                case ROp.Log:   v[o] = Dual.Log(v[ins.A]); break;
                case ROp.Sqrt:  v[o] = Dual.Sqrt(v[ins.A]); break;
                case ROp.Tanh:  v[o] = Dual.Tanh(v[ins.A]); break;
                case ROp.Sin:   v[o] = Dual.Sin(v[ins.A]); break;
                case ROp.Cos:   v[o] = Dual.Cos(v[ins.A]); break;
                case ROp.Abs:   v[o] = Dual.Abs(v[ins.A]); break;
                case ROp.ConstOfValue: v[o] = Dual.Constant(mathFns[ins.Extra](Dual.ValueOf(v[ins.A]))); break;
                case ROp.Sign:  v[o] = Dual.Constant(Math.Sign(Dual.ValueOf(v[ins.A]))); break;
                case ROp.Atan2: v[o] = Dual.Constant(Math.Atan2(Dual.ValueOf(v[ins.A]), Dual.ValueOf(v[ins.B]))); break;
                case ROp.SelectMin: v[o] = Dual.ValueOf(v[ins.A]) <= Dual.ValueOf(v[ins.B]) ? v[ins.A] : v[ins.B]; break;
                case ROp.SelectMax: v[o] = Dual.ValueOf(v[ins.A]) >= Dual.ValueOf(v[ins.B]) ? v[ins.A] : v[ins.B]; break;
                case ROp.Throw: throw exFns[ins.Extra]();
                default: throw new ExpressionException($"Unknown register op: {ins.Op}");
            }
        }
        return v[rootReg];
    }
}
