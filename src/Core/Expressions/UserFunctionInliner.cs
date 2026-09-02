namespace CircuitRF.Core.Expressions;

/// <summary>Raised when an expression's user functions cannot be inlined.</summary>
public sealed class UserFunctionInlineException(string message) : Exception(message);

/// <summary>
/// Substitutes a <see cref="UserFunction"/>'s body at its call site, with the arguments bound.
///
/// <para><b>Why inline rather than resolve.</b> The equation-defined device compiles each of its
/// equations once, into a slot-resolved program with no name lookup left in it, and its three
/// compile paths all end their function switch in "unknown function". Teaching each of them to call
/// a user function means a name lookup per evaluation on the hottest path in the simulator — and
/// they are asked for a value once per time sample, per Newton step, per sweep point.</para>
///
/// <para><b>And inlining is what makes two imported files safe to hold at once.</b>
/// <see cref="UserFunction"/> lives on the TestBench, one flat namespace for the whole design, so
/// two files that each declare a function called <c>ni</c> collide — silently, first one wins.
/// A body substituted at its call site never enters that namespace at all, so the collision is
/// structurally impossible rather than policed.</para>
///
/// <para><b>The cost is the reason for the cap.</b> Substitution DUPLICATES an argument once per
/// occurrence, so a body naming its argument three times, called from a body that does the same, is
/// nine copies — inlining is exponential in nesting depth, and real function libraries nest three
/// deep. The expansion is counted as it is built and refused BY NAME AND BY NUMBER when it runs
/// away, never truncated: a truncated equation is a different device that evaluates.</para>
/// </summary>
public static class UserFunctionInliner
{
    /// <summary>
    /// The expanded-node ceiling.
    ///
    /// <para><b>Set from a measurement, and set far above it on purpose.</b> The worst real function
    /// library available — 102 definitions, mutually nested three deep — expands its heaviest
    /// equation to a few thousand nodes, so this is roughly two orders of magnitude clear of what a
    /// genuine device needs. The gap is deliberate: the failure this guards against is exponential,
    /// so a cap set close to the observed maximum would refuse the next honest library rather than
    /// the runaway it exists for, and a few hundred thousand nodes still compiles in well under a
    /// second.</para>
    /// </summary>
    public const int DefaultNodeLimit = 200_000;

    /// <summary>
    /// Returns <paramref name="ast"/> with every call to a function in <paramref name="functions"/>
    /// replaced by that function's body. Returns the same instance when nothing matched, so a design
    /// that declares no functions pays nothing.
    /// </summary>
    /// <exception cref="UserFunctionInlineException">
    /// The expansion exceeded <paramref name="nodeLimit"/>, a function called itself, or a call site
    /// stated the wrong number of arguments.
    /// </exception>
    public static Expr Inline(Expr ast, IReadOnlyList<UserFunction>? functions,
                              int nodeLimit = DefaultNodeLimit)
    {
        if (functions is null || functions.Count == 0) return ast;

        var byName = new Dictionary<string, UserFunction>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in functions) byName[f.Name] = f;

        var state = new State(byName, nodeLimit);
        Expr result = Walk(ast, state, []);
        return state.Changed ? result : ast;
    }

    private sealed class State(IReadOnlyDictionary<string, UserFunction> byName, int nodeLimit)
    {
        public readonly IReadOnlyDictionary<string, UserFunction> ByName = byName;
        public int Budget = nodeLimit;
        public readonly int Limit = nodeLimit;
        public bool Changed;

        public void Spend(string function)
        {
            if (--Budget >= 0) return;
            throw new UserFunctionInlineException(
                $"'{function}' expands past {Limit:N0} nodes when its definition is substituted at "
                + "its call sites. Function definitions that call one another multiply rather than "
                + "add, so this is a library nested deeply enough that circuitRF cannot write the "
                + "expression out at all — it is refused rather than truncated, because a truncated "
                + "equation is a different device that still evaluates.");
        }
    }

    private static Expr Walk(Expr e, State state, IReadOnlyList<string> stack)
    {
        switch (e)
        {
            case CallExpr call when state.ByName.TryGetValue(call.Name, out var fn):
            {
                if (stack.Any(n => n.Equals(fn.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new UserFunctionInlineException(
                        $"'{fn.Name}' is defined in terms of itself ({string.Join(" → ", stack)} → "
                        + $"{fn.Name}). circuitRF evaluates an expression, not a recursion, so there "
                        + "is no depth at which this terminates.");

                if (call.Args.Length != fn.Parameters.Length)
                    throw new UserFunctionInlineException(
                        $"'{fn.Name}' is declared with {fn.Parameters.Length} parameter(s) and called "
                        + $"with {call.Args.Length}.");

                // The ARGUMENTS are inlined first, in the caller's own stack: they were written at
                // the call site and their own function calls belong to whoever wrote them.
                var args = new Expr[call.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = Walk(call.Args[i], state, stack);

                var bound = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < args.Length; i++) bound[fn.Parameters[i]] = args[i];

                state.Changed = true;
                var nested = new List<string>(stack) { fn.Name };
                return Walk(Substitute(fn.BodyAst, bound, state, fn.Name), state, nested);
            }

            case UnaryExpr u:
                return new UnaryExpr(u.Op, Walk(u.Operand, state, stack));
            case BinaryExpr b:
                return new BinaryExpr(b.Op, Walk(b.Left, state, stack), Walk(b.Right, state, stack));
            case CompareExpr c:
                return new CompareExpr(c.Op, Walk(c.Left, state, stack), Walk(c.Right, state, stack));
            case LogicExpr l:
                return new LogicExpr(l.Op, Walk(l.Left, state, stack), Walk(l.Right, state, stack));
            case ConditionalExpr cd:
                return new ConditionalExpr(Walk(cd.Condition, state, stack),
                                           Walk(cd.Then, state, stack),
                                           Walk(cd.Else, state, stack));
            case CallExpr call:
            {
                var args = new Expr[call.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = Walk(call.Args[i], state, stack);
                return new CallExpr(call.Name, args);
            }
            default:
                return e;
        }
    }

    /// <summary>
    /// A function body with its parameter names replaced by the argument expressions.
    ///
    /// <para>A parameter SHADOWS a design variable of the same name, which is what a parameter is
    /// for — the substitution is by name and the body's own scope is the function's.</para>
    /// </summary>
    private static Expr Substitute(Expr body, IReadOnlyDictionary<string, Expr> bound,
                                   State state, string function)
    {
        state.Spend(function);

        switch (body)
        {
            case RefExpr r when bound.TryGetValue(r.Name, out var arg):
                return arg;
            case UnaryExpr u:
                return new UnaryExpr(u.Op, Substitute(u.Operand, bound, state, function));
            case BinaryExpr b:
                return new BinaryExpr(b.Op, Substitute(b.Left, bound, state, function),
                                            Substitute(b.Right, bound, state, function));
            case CompareExpr c:
                return new CompareExpr(c.Op, Substitute(c.Left, bound, state, function),
                                             Substitute(c.Right, bound, state, function));
            case LogicExpr l:
                return new LogicExpr(l.Op, Substitute(l.Left, bound, state, function),
                                           Substitute(l.Right, bound, state, function));
            case ConditionalExpr cd:
                return new ConditionalExpr(Substitute(cd.Condition, bound, state, function),
                                           Substitute(cd.Then, bound, state, function),
                                           Substitute(cd.Else, bound, state, function));
            case CallExpr call:
            {
                var args = new Expr[call.Args.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = Substitute(call.Args[i], bound, state, function);
                return new CallExpr(call.Name, args);
            }
            default:
                return body;
        }
    }

    /// <summary>Node count of an AST — what the cap is stated in, and what a report quotes.</summary>
    public static int Count(Expr e) => e switch
    {
        UnaryExpr u       => 1 + Count(u.Operand),
        BinaryExpr b      => 1 + Count(b.Left) + Count(b.Right),
        CompareExpr c     => 1 + Count(c.Left) + Count(c.Right),
        LogicExpr l       => 1 + Count(l.Left) + Count(l.Right),
        ConditionalExpr d => 1 + Count(d.Condition) + Count(d.Then) + Count(d.Else),
        CallExpr call     => 1 + call.Args.Sum(Count),
        _                 => 1,
    };
}
