namespace CircuitRF.Core.Expressions;

/// <summary>
/// How a SITE unit — the unit a field or a binding was typed in — meets an expression (expressions.md §8).
///
/// <para><b>Var-unit-wins.</b> An expression that is UNIT-BEARING — it contains a unit literal (<c>10um</c>), or it
/// names a binding that carries a unit (directly, or through unit-less bindings whose own text holds a literal) — is
/// already in base SI, so the site unit is not applied to its result.</para>
///
/// <para><b>A bare operand beside a unit-bearing one takes the site unit</b> (brief-units-in-expressions Q1 (b)). In
/// <c>cav_w + 40</c> typed in mil, the 40 is forty MIL, not forty metres. The rule is applied as an AST transform
/// before evaluation — never a text rewrite — to the operands of <c>+</c>, <c>-</c>, a comparison, a conditional's
/// two branches and <c>min</c>/<c>max</c>. A multiplicative operand stays dimensionless (<c>2*w</c>, <c>w/4</c>).</para>
///
/// <para><b>Powers are tracked, not refused.</b> Each unit-bearing sub-expression carries the power of the site
/// dimension it holds — a literal or a unit-bearing name is 1, <c>w*w</c> is 2, <c>w/h</c> is 0, <c>sqrt(w*w)</c> is
/// 1, <c>w^3</c> is 3 — and a bare operand beside it is scaled by <c>site^power</c>, so <c>sqrt(w*w + 25)</c> adds 25
/// square mil, and <c>w/h + 1</c> adds exactly 1. Refusing every bare literal beside a non-unit power was the other
/// option the brief offered; it would refuse the ordinary <c>w/h + 1</c>, which tracking gets right for free. A power
/// that cannot be known — a unit-bearing argument of a function other than those above, <c>w^n</c> with a non-constant
/// <c>n</c> — leaves its bare siblings in base SI, which is what they meant before this rule existed.</para>
/// </summary>
public sealed partial class Evaluator
{
    /// <summary>
    /// The var-unit-wins test: true when <paramref name="ast"/> contains a unit literal or names a unit-bearing
    /// binding in <paramref name="scope"/>. A binding is unit-bearing when it states a unit, or when it states none
    /// and its own expression is unit-bearing by a LITERAL (followed through unit-less bindings) — such a binding's
    /// value is base SI all the same. A unit-less binding that merely references a unit-bearing name is NOT followed:
    /// that was already the rule before literals existed, and following it would change existing documents' values.
    /// </summary>
    public static bool IsUnitBearing(Expr ast, Scope scope)
    {
        // A distribution call is its nominal argument as far as units go — its spread must not decide it.
        ast = NominalView(ast);
        return ContainsUnitLiteral(ast) || AstWalker.CollectRefs(ast).Any(n => NameIsUnitBearing(n, scope, null));
    }

    /// <summary><see cref="IsUnitBearing(Expr, Scope)"/> for expression text; false when the text does not parse.</summary>
    public static bool IsUnitBearing(string expression, Scope scope)
    {
        try { return IsUnitBearing(Parser.Parse(expression), scope); }
        catch { return false; }
    }

    /// <summary>
    /// The unit a base-SI number standing in for binding <paramref name="name"/> must state to stay unit-bearing exactly
    /// when the binding is: null for a bare binding; otherwise the scale-1 base (<see cref="Units.BaseUnit"/>) of the unit
    /// its value carries — its stated unit, or the first unit literal it reaches through unit-less bindings. A resolved
    /// value rebound with no unit turns a unit-bearing name bare, and a site unit var-unit-wins skipped (an element field
    /// typed in pF that names it) then scales the number a second time. The parametric sweep's re-injection is the same
    /// rule for a stated unit.
    /// </summary>
    public static string? BaseUnitStandIn(string name, Scope scope)
    {
        if (!NameIsUnitBearing(name, scope, null)) return null;
        return CarriedUnit(name, scope, new HashSet<string>(StringComparer.Ordinal)) is { } u ? Units.BaseUnit(u) : null;
    }

    // The unit a unit-bearing binding's value carries, followed exactly as LiteralBearing follows it.
    private static string? CarriedUnit(string name, Scope scope, HashSet<string> visiting)
    {
        if (scope.Lookup(name) is not { } found) return null;
        if (!string.IsNullOrEmpty(found.Unit)) return found.Unit;
        if (!visiting.Add($"{found.Owner.DebugName}::{name}")) return null;
        Expr ast;
        try { ast = Parser.Parse(found.Expression); }
        catch { return null; }
        string? literal = null;
        if (AnyNode(ast, e => e is NumberExpr { Unit: { } u } && (literal = u) is not null)) return literal;
        foreach (var r in AstWalker.CollectRefs(ast))
            if (found.Owner.Lookup(r) is { } f && string.IsNullOrEmpty(f.Unit) && CarriedUnit(r, found.Owner, visiting) is { } carried)
                return carried;
        return null;
    }

    /// <summary>True when <paramref name="ast"/> holds a unit literal anywhere.</summary>
    public static bool ContainsUnitLiteral(Expr ast) => AnyNode(ast, e => e is NumberExpr { Unit: not null });

    private static bool NameIsUnitBearing(string name, Scope scope, HashSet<string>? visiting)
    {
        if (scope.Lookup(name) is not { } found) return false;
        if (!string.IsNullOrEmpty(found.Unit)) return true;
        return LiteralBearing(name, found.Expression, found.Owner, visiting);
    }

    // A unit-less binding whose text holds a unit literal, directly or through other unit-less bindings.
    private static bool LiteralBearing(string name, string expression, Scope owner, HashSet<string>? visiting)
    {
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visiting.Add($"{owner.DebugName}::{name}")) return false;   // a cycle is Resolve's to report
        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return false; }
        if (ContainsUnitLiteral(ast)) return true;
        foreach (var r in AstWalker.CollectRefs(ast))
            if (owner.Lookup(r) is { } f && string.IsNullOrEmpty(f.Unit) && LiteralBearing(r, f.Expression, f.Owner, visiting))
                return true;
        return false;
    }

    /// <summary>
    /// <paramref name="ast"/> with every bare operand beside a unit-bearing one scaled by the site unit
    /// <paramref name="unit"/> raised to that operand's power. Returns <paramref name="ast"/> itself when the site
    /// unit is not a linear multiplier other than 1 (none, a base symbol, dBm) — there is nothing to scale by.
    /// Callers apply it only where var-unit-wins has already decided not to scale the result.
    /// </summary>
    public static Expr ScaleBareOperands(Expr ast, Scope scope, string? unit)
        => ScaleBareOperands(ast, name => NameIsUnitBearing(name, scope, null) ? NamePower(name, scope) : null, unit);

    /// <summary>
    /// <see cref="ScaleBareOperands(Expr, Scope, string?)"/> with the unit-bearing names supplied by a delegate that
    /// returns a name's power of the site dimension, or null for a bare name — for a caller that has no
    /// <see cref="Scope"/> (FreqUnit's globals).
    /// </summary>
    public static Expr ScaleBareOperands(Expr ast, Func<string, double?> namePower, string? unit)
    {
        if (string.IsNullOrEmpty(unit) || Units.Scale(unit) is not { } s || s == 1.0) return ast;
        return new BareOperandScaler(namePower, unit).Scale(ast).Expr;
    }

    // A unit-bearing name's power: 1 for a binding that states its unit (a unit field names one unit); for a unit-less
    // binding that is unit-bearing through literals, the power its own expression carries.
    private static double? NamePower(string name, Scope scope, HashSet<string>? visiting = null)
    {
        if (scope.Lookup(name) is not { } found) return null;
        if (!string.IsNullOrEmpty(found.Unit)) return 1;
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visiting.Add($"{found.Owner.DebugName}::{name}")) return double.NaN;
        Expr ast;
        try { ast = Parser.Parse(found.Expression); }
        catch { return null; }
        var owner = found.Owner;
        var c = new BareOperandScaler(n => NameIsUnitBearing(n, owner, null) ? NamePower(n, owner, visiting) : null, null).Scale(ast);
        return c.Bare ? null : c.Power;
    }

    private static bool AnyNode(Expr e, Func<Expr, bool> test)
    {
        if (test(e)) return true;
        return e switch
        {
            UnaryExpr u       => AnyNode(u.Operand, test),
            BinaryExpr b      => AnyNode(b.Left, test) || AnyNode(b.Right, test),
            CompareExpr c     => AnyNode(c.Left, test) || AnyNode(c.Right, test),
            LogicExpr l       => AnyNode(l.Left, test) || AnyNode(l.Right, test),
            ConditionalExpr d => AnyNode(d.Condition, test) || AnyNode(d.Then, test) || AnyNode(d.Else, test),
            CallExpr cl       => cl.Args.Any(a => AnyNode(a, test)),
            PortRangeExpr pr  => AnyNode(pr.Lo, test) || AnyNode(pr.Hi, test),
            IndexExpr ix      => AnyNode(ix.Target, test)
                                 || ix.Tokens.Any(t => (t.A is { } a && AnyNode(a, test)) || (t.B is { } b && AnyNode(b, test))),
            _                 => false,
        };
    }

    /// <summary>
    /// One pass over an AST that classifies every sub-expression — BARE (no unit literal, no unit-bearing name), or
    /// unit-bearing with a known power of the site dimension (NaN when it cannot be known) — and, when a site unit is
    /// given, rebuilds it with bare operands scaled. With a null unit it only classifies.
    /// </summary>
    private sealed class BareOperandScaler(Func<string, double?> namePower, string? unit)
    {
        public readonly record struct Result(Expr Expr, bool Bare, double Power)
        {
            public static Result OfBare(Expr e) => new(e, true, 0);
            public static Result Of(Expr e, double power) => new(e, false, power);
        }

        public Result Scale(Expr e)
        {
            switch (e)
            {
                case NumberExpr n:
                    return n.Unit is null ? Result.OfBare(n) : Result.Of(n, 1);
                case RefExpr r:
                    return namePower(r.Name) is { } p ? Result.Of(r, p) : Result.OfBare(r);
                case UnaryExpr u:
                {
                    var o = Scale(u.Operand);
                    var ue = Same(u.Operand, o.Expr) ? u : u with { Operand = o.Expr };
                    return u.Op == "!" || o.Bare ? Result.OfBare(ue) : Result.Of(ue, o.Power);
                }
                case BinaryExpr { Op: "+" or "-" } b:
                {
                    var (l, r) = Pair(Scale(b.Left), Scale(b.Right));
                    var be = Same(b.Left, l.Expr) && Same(b.Right, r.Expr) ? b : b with { Left = l.Expr, Right = r.Expr };
                    return l.Bare && r.Bare ? Result.OfBare(be) : Result.Of(be, l.Bare ? r.Power : l.Power);
                }
                case BinaryExpr b:
                {
                    var l = Scale(b.Left);
                    var r = Scale(b.Right);
                    var be = Same(b.Left, l.Expr) && Same(b.Right, r.Expr) ? b : b with { Left = l.Expr, Right = r.Expr };
                    if (l.Bare && r.Bare) return Result.OfBare(be);
                    double power = b.Op switch
                    {
                        "*" => l.Power + r.Power,
                        "/" => l.Power - r.Power,
                        "^" => !l.Bare && r.Bare && ConstantOf(b.Right) is { } k ? l.Power * k : double.NaN,
                        _   => double.NaN,
                    };
                    return Result.Of(be, power);
                }
                case CompareExpr c:
                {
                    var (l, r) = Pair(Scale(c.Left), Scale(c.Right));
                    return Result.OfBare(Same(c.Left, l.Expr) && Same(c.Right, r.Expr) ? c : c with { Left = l.Expr, Right = r.Expr });
                }
                case LogicExpr lg:
                {
                    var l = Scale(lg.Left);
                    var r = Scale(lg.Right);
                    return Result.OfBare(Same(lg.Left, l.Expr) && Same(lg.Right, r.Expr) ? lg : lg with { Left = l.Expr, Right = r.Expr });
                }
                case ConditionalExpr cd:
                {
                    var cond = Scale(cd.Condition);
                    var (t, f) = Pair(Scale(cd.Then), Scale(cd.Else));
                    var ce = Same(cd.Condition, cond.Expr) && Same(cd.Then, t.Expr) && Same(cd.Else, f.Expr)
                        ? cd : new ConditionalExpr(cond.Expr, t.Expr, f.Expr);
                    return t.Bare && f.Bare ? Result.OfBare(ce) : Result.Of(ce, t.Bare ? f.Power : t.Power);
                }
                case CallExpr cl:
                    return Call(cl);
                case IndexExpr ix:
                {
                    var target = Scale(ix.Target);
                    var ie = Same(ix.Target, target.Expr) ? ix : ix with { Target = target.Expr };
                    return target.Bare ? Result.OfBare(ie) : Result.Of(ie, target.Power);
                }
                default:
                    return Result.OfBare(e);   // ConstExpr, StringLiteralExpr
            }
        }

        private Result Call(CallExpr cl)
        {
            if (IsStatisticalCall(cl)) return Distribution(cl);
            var args = cl.Args.Select(Scale).ToArray();
            string name = cl.Name;
            if (name is "min" or "max" && args.Length > 0 && args.FirstOrDefault(a => !a.Bare) is { Bare: false } bearing)
                for (int i = 0; i < args.Length; i++)
                    if (args[i].Bare) args[i] = Result.OfBare(Scaled(args[i].Expr, bearing.Power));
            var ce = args.Select((a, i) => Same(cl.Args[i], a.Expr)).All(x => x) ? cl : cl with { Args = [.. args.Select(a => a.Expr)] };
            if (args.All(a => a.Bare)) return Result.OfBare(ce);
            double power = name switch
            {
                "min" or "max"               => args.First(a => !a.Bare).Power,
                "abs" or "real"             => args.Length == 1 ? args[0].Power : double.NaN,
                "sqrt"                       => args.Length == 1 ? args[0].Power / 2 : double.NaN,
                "pow"                        => args.Length == 2 && !args[0].Bare && args[1].Bare && ConstantOf(cl.Args[1]) is { } k
                                                    ? args[0].Power * k : double.NaN,
                _                            => double.NaN,
            };
            return Result.Of(ce, power);
        }

        // A distribution is its nominal for units: bare or bearing as its first argument is. An ABSOLUTE spread
        // (agauss/aunif/limit's second argument) is in the nominal's unit, so a bare one beside a unit-bearing
        // nominal takes the site unit like any operand that must agree with it; a relative spread and k are
        // dimensionless and are left alone.
        private Result Distribution(CallExpr cl)
        {
            if (cl.Args.Length == 0) return Result.OfBare(cl);
            var nominal = Scale(cl.Args[0]);
            var args    = cl.Args.ToArray();
            args[0] = nominal.Expr;
            if (!nominal.Bare && cl.Name is "agauss" or "aunif" or "limit" && args.Length > 1)
            {
                var spread = Scale(cl.Args[1]);
                args[1] = spread.Bare ? Scaled(spread.Expr, nominal.Power) : spread.Expr;
            }
            var ce = args.Select((a, i) => Same(cl.Args[i], a)).All(x => x) ? cl : cl with { Args = args };
            return nominal.Bare ? Result.OfBare(ce) : Result.Of(ce, nominal.Power);
        }

        // Two operands that must agree in dimension: a bare one beside a unit-bearing one of known power takes the site
        // unit to that power.
        private (Result, Result) Pair(Result l, Result r)
        {
            if (l.Bare && !r.Bare) l = Result.OfBare(Scaled(l.Expr, r.Power));
            else if (r.Bare && !l.Bare) r = Result.OfBare(Scaled(r.Expr, l.Power));
            return (l, r);
        }

        // A bare operand in the site unit to the given power: a bare number becomes the literal it would have been had
        // it been typed with the unit (`40` → `40mil`), anything else is multiplied by `1mil` (or `1mil^k`).
        private Expr Scaled(Expr bare, double power)
        {
            if (unit is null || double.IsNaN(power) || power == 0) return bare;
            double s = Units.Scale(unit)!.Value;
            if (power == 1 && bare is NumberExpr n)
                return new NumberExpr(n.Value * s, unit);
            Expr one = new NumberExpr(s, unit);
            return new BinaryExpr("*", bare, power == 1 ? one : new BinaryExpr("^", one, new NumberExpr(power)));
        }

        // An exponent written as a constant: `2`, `-1`, `0.5`, `1/2`.
        private static double? ConstantOf(Expr e) => e switch
        {
            NumberExpr { Unit: null } n              => n.Value,
            UnaryExpr { Op: "-" } u                  => -ConstantOf(u.Operand),
            UnaryExpr { Op: "+" } u                  => ConstantOf(u.Operand),
            BinaryExpr { Op: "/" } d when ConstantOf(d.Left) is { } a && ConstantOf(d.Right) is { } b && b != 0 => a / b,
            BinaryExpr { Op: "*" } m when ConstantOf(m.Left) is { } a && ConstantOf(m.Right) is { } b => a * b,
            _ => null,
        };

        private static bool Same(Expr a, Expr b) => ReferenceEquals(a, b);
    }
}
