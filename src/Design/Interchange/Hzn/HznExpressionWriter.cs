// HZN-4 R-hzn4-1 — a circuitRF expression, written in Hertzon's spelling.
//
// The inverse of HznExpressions.cs, and like it never string substitution: the expression is parsed by the one engine,
// the site unit it was typed in is applied to the TREE (C3dResolution.AtSite), and the tree is printed with every unit
// spelled out, because Hertzon has no site unit — a bare number beside a quantity is a plain number there. What the
// printed text means was established by `hertzon-cli describe` (src/Design/RESOLVED.md §HZN): `**` is the power
// operator, `and`/`or` the logical ones, `log` the base-10 logarithm and `ln` the natural one, `1m` a metre.
//
// A spelling Hertzon is not known to have is a refusal item, never a guess: the unit, constant or function is named.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed class HznExpressionWriter
{
    private readonly C3dResolution _resolution;
    private readonly HznRefusalBuilder _refusals;
    private readonly Func<string, string> _rename;
    private string _user = "";
    private bool _failed;

    /// <summary>Set when an <c>if(...)</c> was written: Hertzon's tutorial names it, but no evaluation of it could be checked.</summary>
    public bool WroteConditional { get; private set; }

    public HznExpressionWriter(C3dResolution resolution, HznRefusalBuilder refusals, Func<string, string> rename)
    {
        _resolution = resolution;
        _refusals = refusals;
        _rename = rename;
    }

    /// <summary>Hertzon's spelling of an engine unit, for the units `hertzon-cli describe` accepted.</summary>
    private static readonly Dictionary<string, string> Units = new(StringComparer.Ordinal)
    {
        ["metre"] = "m", ["mm"] = "mm", ["um"] = "um", ["cm"] = "cm", ["nm"] = "nm", ["mil"] = "mil", ["in"] = "in", ["inch"] = "in",
        ["deg"] = "deg", ["rad"] = "rad",
        ["Hz"] = "Hz", ["kHz"] = "kHz", ["MHz"] = "MHz", ["GHz"] = "GHz", ["THz"] = "THz",
        ["ohm"] = "ohm", ["Ohm"] = "ohm", ["ohms"] = "ohm", ["Ohms"] = "ohm",
        ["s"] = "s", ["ns"] = "ns", ["ps"] = "ps",
        ["H"] = "H", ["mH"] = "mH", ["uH"] = "uH", ["nH"] = "nH", ["pH"] = "pH",
        ["F"] = "F", ["mF"] = "mF", ["uF"] = "uF", ["nF"] = "nF", ["pF"] = "pF",
    };

    private static readonly Dictionary<string, string> Functions = new(StringComparer.Ordinal)
    {
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["asin"] = "asin", ["acos"] = "acos", ["atan"] = "atan", ["atan2"] = "atan2",
        ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh", ["exp"] = "exp", ["sign"] = "sign",
        ["sqrt"] = "sqrt", ["abs"] = "abs", ["floor"] = "floor", ["ceil"] = "ceil", ["pow"] = "pow", ["min"] = "min", ["max"] = "max",
        // circuitRF's log is the natural logarithm; Hertzon's log is base 10 and its ln is natural.
        ["ln"] = "ln", ["log"] = "ln", ["log10"] = "log",
    };

    /// <summary>
    /// <paramref name="expression"/> as typed at <paramref name="unit"/> (a stored spelling, or null), in Hertzon's spelling; null
    /// with a refusal item when it cannot be written. <paramref name="user"/> names the object or variable that uses it.
    /// </summary>
    public (string Text, bool Bearing)? Write(string expression, string? unit, string user)
    {
        _user = user;
        _failed = false;
        Expr ast;
        bool bearing;
        try { (ast, bearing) = _resolution.AtSite(expression, unit); }
        catch (ExpressionException e)
        {
            _refusals.Add(HznRefusalKind.Expression, "expression", $"'{expression}' does not parse here: {e.Message}", user);
            return null;
        }
        string text = Print(ast, 0);
        return _failed ? null : (text, bearing);
    }

    // Precedence of the node a text stands for: higher binds tighter.
    private const int Or = 1, And = 2, Compare = 3, Sum = 4, Product = 5, Unary = 6, Power = 7, Primary = 9;

    private string Print(Expr e, int context)
    {
        (string text, int level) = Node(e);
        return level < context ? "(" + text + ")" : text;
    }

    private (string, int) Node(Expr e)
    {
        switch (e)
        {
            case NumberExpr n:
                return Number(n);
            case ConstExpr c:
                switch (c.Name)
                {
                    case "pi": return ("pi", Primary);
                    case "e": return ("2.718281828459045", Primary);
                    default: Refuse("constant " + c.Name, $"Hertzon has no constant '{c.Name}'"); return ("0", Primary);
                }
            case RefExpr r:
                return (_rename(r.Name), Primary);
            case UnaryExpr { Op: "-" or "+" } u:
                return (u.Op + Print(u.Operand, Unary), Unary);
            case UnaryExpr u:
                Refuse("operator " + u.Op, "Hertzon does not accept it in an expression");
                return ("0", Primary);
            case BinaryExpr b:
                switch (b.Op)
                {
                    case "+" or "-":
                        return (Print(b.Left, Sum) + b.Op + Print(b.Right, Sum + 1), Sum);
                    case "*" or "/":
                        return (Print(b.Left, Product) + b.Op + Print(b.Right, Product + 1), Product);
                    case "^":
                        // `-2**2` is -4 in Hertzon as it is here, so a unary or a power on the left keeps its parentheses.
                        return (Print(b.Left, Power + 1) + "**" + Print(b.Right, Unary), Power);
                    default:
                        Refuse("operator " + b.Op, "Hertzon has no such operator");
                        return ("0", Primary);
                }
            case CompareExpr c:
                return (Print(c.Left, Sum) + c.Op + Print(c.Right, Sum), Compare);
            case LogicExpr l:
                return (Print(l.Left, l.Op == "&&" ? And : Or) + (l.Op == "&&" ? " and " : " or ") + Print(l.Right, l.Op == "&&" ? And + 1 : Or + 1),
                        l.Op == "&&" ? And : Or);
            case ConditionalExpr d:
                WroteConditional = true;
                return ($"if({Print(d.Condition, 0)},{Print(d.Then, 0)},{Print(d.Else, 0)})", Primary);
            case CallExpr f:
                if (!Functions.TryGetValue(f.Name, out string? hz))
                {
                    Refuse("function " + f.Name, $"Hertzon has no function '{f.Name}', or one that means the same");
                    return ("0", Primary);
                }
                return ($"{hz}({string.Join(",", f.Args.Select(a => Print(a, 0)))})", Primary);
            default:
                Refuse("expression", $"a {e.GetType().Name.Replace("Expr", "")} has no Hertzon spelling");
                return ("0", Primary);
        }
    }

    private (string, int) Number(NumberExpr n)
    {
        string text;
        if (n.Unit is null) text = Digits(n.Value);
        else if (!Units.TryGetValue(n.Unit, out string? hz) || CircuitRF.Core.Expressions.Units.Scale(n.Unit) is not { } scale)
        {
            Refuse("unit " + n.Unit, $"'{n.Unit}' is not a unit Hertzon was seen to accept");
            return ("0", Primary);
        }
        else text = Digits(n.Value / scale) + hz;
        return n.Value < 0 || text.StartsWith('-') ? (text, Unary) : (text, Primary);
    }

    /// <summary>The digits of <paramref name="v"/> with the noise a division leaves removed: twelve significant digits when
    /// they read back to the same double within a few ulps, else all seventeen.</summary>
    public static string Digits(double v)
    {
        string full = v.ToString("R", CultureInfo.InvariantCulture);
        string twelve = v.ToString("G12", CultureInfo.InvariantCulture);
        if (double.TryParse(twelve, NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
            && Math.Abs(back - v) <= 8 * Math.Abs(v) * double.Epsilon + 1e-13 * Math.Abs(v)) full = twelve;
        return full.Contains('E') ? full.Replace("E+", "e+").Replace("E-", "e-") : full;
    }

    private void Refuse(string what, string why)
    {
        _failed = true;
        _refusals.Add(HznRefusalKind.Expression, what, why, _user);
    }
}
