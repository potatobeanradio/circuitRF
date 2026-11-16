using System.Globalization;
using System.Text;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// circuitRF's expression AST written as the dialect's behavioural-source expression — the inverse of
/// <see cref="SpiceExpression"/>, function by function (brief TRN-6 R-trn6-4). Every spelling here is the SAME function on the
/// same domain; a function with no such spelling is refused by name, never approximated (D7).
///
/// <para><b>Two of the dialect's behavioural-source functions are not the mathematical ones, and each is spelled around:</b>
/// its <c>exp</c> grows linearly once the argument passes 14, so circuitRF's <c>exp(x)</c> is written <c>pow(e, x)</c>; and its
/// <c>pow</c>, <c>**</c> and <c>^</c> all take the magnitude of the base, so <c>x^n</c> with an odd integer <c>n</c> is
/// written <c>x*pow(x, n−1)</c>, which keeps the sign. An exponent that is not a constant could be either, so it is
/// refused. <c>tanh</c> and <c>sign</c> are not behavioural-source functions in the dialect and are written through the exact
/// identities <c>1 − 2/(e^(2x) + 1)</c> and a conditional.</para>
/// </summary>
public static class SpiceExpressionWriter
{
    /// <summary>e as the nearest double, the base every exponential is written over.</summary>
    public const string E = "2.718281828459045";
    private const string Pi = "3.141592653589793";

    /// <summary>
    /// Writes <paramref name="e"/>. <paramref name="reference"/> spells a name the expression reads (a port voltage
    /// <c>_v1</c> as <c>V(a,b)</c>, a control current, <c>time</c>), or returns null for a name the dialect cannot read, which
    /// refuses the expression. Returns null and sets <paramref name="refusal"/> when the expression cannot be written.
    /// </summary>
    public static string? Write(Expr e, Func<string, string?> reference, out string? refusal)
    {
        var sb = new StringBuilder();
        refusal = null;
        try { W(e, sb, reference); }
        catch (Refused r) { refusal = r.Message; return null; }
        return sb.ToString();
    }

    private sealed class Refused(string message) : Exception(message);

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void W(Expr e, StringBuilder sb, Func<string, string?> reference)
    {
        switch (e)
        {
            case NumberExpr n:
                if (!double.IsFinite(n.Value)) throw new Refused($"its expression holds a non-finite number ({n.Value})");
                sb.Append('(').Append(Num(n.Value)).Append(')');
                return;
            case ConstExpr c:
                switch (c.Name)
                {
                    case "pi": sb.Append(Pi); return;
                    case "e": sb.Append(E); return;
                    default: throw new Refused($"its expression reads the constant '{c.Name}', which a time-domain expression cannot hold");
                }
            case RefExpr r:
                sb.Append(reference(r.Name) ?? throw new Refused($"its expression reads '{r.Name}', which the SPICE dialect cannot name"));
                return;
            case UnaryExpr u:
                if (u.Op is not ("-" or "+" or "!")) throw new Refused($"its expression uses the operator '{u.Op}'");
                sb.Append('(').Append(u.Op);
                W(u.Operand, sb, reference);
                sb.Append(')');
                return;
            case BinaryExpr b when b.Op == "^":
                Power(b.Left, b.Right, sb, reference);
                return;
            case BinaryExpr b:
                if (b.Op is not ("+" or "-" or "*" or "/")) throw new Refused($"its expression uses the operator '{b.Op}'");
                Bin(b.Left, b.Op, b.Right, sb, reference);
                return;
            case CompareExpr c:
                Bin(c.Left, c.Op, c.Right, sb, reference);
                return;
            case LogicExpr l:
                Bin(l.Left, l.Op, l.Right, sb, reference);
                return;
            case ConditionalExpr cd:
                Ternary(cd.Condition, cd.Then, cd.Else, sb, reference);
                return;
            case CallExpr call:
                Call(call, sb, reference);
                return;
            default:
                throw new Refused($"its expression holds a construct the SPICE dialect has no spelling for ({e.GetType().Name})");
        }
    }

    private static void Bin(Expr l, string op, Expr r, StringBuilder sb, Func<string, string?> reference)
    {
        sb.Append('(');
        W(l, sb, reference);
        sb.Append(op);
        W(r, sb, reference);
        sb.Append(')');
    }

    // The dialect's parser needs a space before '?' to tell it from other tokens (its manual says so).
    private static void Ternary(Expr c, Expr a, Expr b, StringBuilder sb, Func<string, string?> reference)
    {
        sb.Append('(');
        W(c, sb, reference);
        sb.Append(" ? ");
        W(a, sb, reference);
        sb.Append(" : ");
        W(b, sb, reference);
        sb.Append(')');
    }

    private static void Power(Expr x, Expr n, StringBuilder sb, Func<string, string?> reference)
    {
        if (Constant(n) is not double k)
            throw new Refused("its expression raises to a power that is not a constant: the SPICE dialect's power takes the magnitude "
                            + "of its base, so the sign of a negative base would depend on the exponent's value");
        bool oddInteger = k == Math.Floor(k) && Math.Abs(k) < 1e15 && Math.Abs(k % 2) == 1;
        if (!oddInteger)
        {
            sb.Append("pow(");
            W(x, sb, reference);
            sb.Append(',').Append(Num(k)).Append(')');
            return;
        }
        // x^n = x * |x|^(n-1) for odd n: the dialect's pow takes |x|, and an even power of |x| is the even power of x.
        sb.Append('(');
        W(x, sb, reference);
        sb.Append("*pow(");
        W(x, sb, reference);
        sb.Append(',').Append(Num(k - 1)).Append("))");
    }

    private static double? Constant(Expr e) => e switch
    {
        NumberExpr n => n.Value,
        UnaryExpr { Op: "-" } u when Constant(u.Operand) is double v => -v,
        UnaryExpr { Op: "+" } u => Constant(u.Operand),
        _ => null,
    };

    private static void Call(CallExpr call, StringBuilder sb, Func<string, string?> reference)
    {
        string name = call.Name.ToLowerInvariant();
        void Need(int n)
        {
            if (call.Args.Length != n) throw new Refused($"its expression calls {call.Name} with {call.Args.Length} argument(s)");
        }
        void Plain(string spelling)
        {
            sb.Append(spelling).Append('(');
            for (int i = 0; i < call.Args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                W(call.Args[i], sb, reference);
            }
            sb.Append(')');
        }
        switch (name)
        {
            case "exp":
                Need(1);
                sb.Append("pow(").Append(E).Append(',');
                W(call.Args[0], sb, reference);
                sb.Append(')');
                return;
            case "log" or "ln": Need(1); Plain("ln"); return;
            case "log10" or "sqrt" or "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "sinh" or "cosh"
                or "abs" or "floor" or "ceil":
                Need(1); Plain(name); return;
            case "min" or "max": Need(2); Plain(name); return;
            case "pow": Need(2); Power(call.Args[0], call.Args[1], sb, reference); return;
            case "tanh":
                Need(1);
                // tanh x = 1 − 2/(e^(2x) + 1): exact, and finite at both ends (e^(2x) overflows to +inf, giving 1).
                sb.Append("(1-2/(pow(").Append(E).Append(",2*");
                W(call.Args[0], sb, reference);
                sb.Append(")+1))");
                return;
            case "sign":
                Need(1);
                sb.Append('(');
                W(call.Args[0], sb, reference);
                sb.Append(">0 ? 1 : (");
                W(call.Args[0], sb, reference);
                sb.Append("<0 ? -1 : 0))");
                return;
            case "if":
                Need(3);
                Ternary(call.Args[0], call.Args[1], call.Args[2], sb, reference);
                return;
            default:
                throw new Refused($"its expression calls {call.Name}(), which has no spelling in the SPICE dialect's behavioural source");
        }
    }
}
