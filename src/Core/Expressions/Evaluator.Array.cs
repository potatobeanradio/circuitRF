// brief-paa-4 R-paa4-3 — the weight functions of a Phased Antenna Array, valid anywhere (docs/design/phased-array.md §4.3):
//
//   steer(theta0, phi0, f0[, x, y])   degrees, −360·f0/c·(x·sinθ0·cosφ0 + y·sinθ0·sinφ0): the phase that points a phase-shifter beam;
//   steerDelay(theta0, phi0[, x, y])  seconds, −(x·sinθ0·cosφ0 + y·sinθ0·sinφ0)/c: the delay that points a true-time-delay beam;
//   taylor(sll, nbar), chebyshev(sll), cosq(q)   separable amplitude tapers over rows and columns, 1 at the peak.
//
// The angles are angles as any trig argument's are: a unit-bearing value (30 deg) is already radians, a bare number is radians. f0 is
// hertz (a unit-bearing VAR already is). x and y default to the element's in element scope and are required elsewhere; the tapers read
// the element's row and column and the array's Rows and Cols, so outside element scope they are refused by name.
//
// THEIR RESULTS ARE NUMBERS IN THEIR OWN UNIT, NOT THEIR ARGUMENTS'. steer's θ0 is unit-bearing, but its value is degrees, a bare number
// a Phase field's degree site unit must apply to. So for the site-unit rule (Evaluator.SiteUnit.cs) a weight call is opaque: steer and the
// tapers are bare, steerDelay is seconds, base SI — WeightView is the AST the var-unit-wins test sees.
//
// PHASE TAKES DEGREES AS ITS SITE UNIT (EvalPhase), as the Combiner's Phase[k] does, and the ordinary var-unit-wins test is the wrong one
// for it: `360*x/(10 mm)` is unit-bearing by its literal, but its unit is a length's and the value is degrees. So the test is narrower —
// an expression that carries an ANGLE (a literal or a VAR whose unit is one) is already radians and goes through the ordinary rule; any
// other is a number of degrees.

using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Core.Expressions;

public sealed partial class Evaluator
{
    /// <summary>The weight functions, by name.</summary>
    public static bool IsWeightFunction(string name) => name is "steer" or "steerDelay" or "taylor" or "chebyshev" or "cosq";

    private Value EvalWeightFunction(CallExpr cl, Scope scope)
    {
        bool element = ElementScope.IsElement(scope);
        switch (cl.Name)
        {
            case "steer":
            case "steerDelay":
            {
                int angles = 2, lo = cl.Name == "steer" ? 3 : 2;
                if (cl.Args.Length != lo && cl.Args.Length != lo + 2)
                    throw new ArityException(cl.Name, lo, cl.Args.Length);
                double theta0 = Real(cl, cl.Args[0], scope, "theta0"), phi0 = Real(cl, cl.Args[1], scope, "phi0");
                double f0 = cl.Name == "steer" ? Real(cl, cl.Args[angles], scope, "f0") : 0;
                double x, y;
                if (cl.Args.Length == lo + 2)
                {
                    x = Real(cl, cl.Args[lo], scope, "x");
                    y = Real(cl, cl.Args[lo + 1], scope, "y");
                }
                else if (element)
                {
                    x = Resolve("x", scope).AsReal();
                    y = Resolve("y", scope).AsReal();
                }
                else
                {
                    string args = cl.Name == "steer" ? "theta0, phi0, f0" : "theta0, phi0";
                    throw new ExpressionException($"{cl.Name}({args}) takes the element's position x, y only in a Phased Antenna Array's Amp, Phase or Delay " +
                                                  $"or its channel cell; anywhere else write {cl.Name}({args}, x, y) with the position in metres.");
                }
                double path = x * Math.Sin(theta0) * Math.Cos(phi0) + y * Math.Sin(theta0) * Math.Sin(phi0);
                return cl.Name == "steer"
                    ? new Value(-360 * f0 / SpeedOfLight * path)
                    : new Value(-path / SpeedOfLight);
            }
            default:
            {
                int arity = cl.Name == "taylor" ? 2 : 1;
                if (cl.Args.Length != arity) throw new ArityException(cl.Name, arity, cl.Args.Length);
                if (!element)
                    throw new ExpressionException($"{cl.Name}() is a taper over an array's rows and columns, so it is valid only in a Phased Antenna Array's Amp " +
                                                  "(or its [n] overrides) or its channel cell, where it reads the element's row and column.");
                int row = (int)Resolve("row", scope).AsReal(), col = (int)Resolve("col", scope).AsReal();
                int rows = (int)Resolve(ElementScope.RowsName, scope).AsReal(), cols = (int)Resolve(ElementScope.ColsName, scope).AsReal();
                double a = Real(cl, cl.Args[0], scope, cl.Name == "cosq" ? "q" : "sll");
                double[] lr, lc;
                switch (cl.Name)
                {
                    case "taylor":
                    {
                        double nb = Real(cl, cl.Args[1], scope, "nbar");
                        if (!(nb >= 1) || nb != Math.Round(nb) || nb > 64)
                            throw new TypeErrorException($"taylor(sll, nbar): nbar is a whole number from 1 to 64, not {nb}.");
                        if (!(Math.Abs(a) > 0)) throw new TypeErrorException("taylor(sll, nbar): sll is the sidelobe level in dB, such as -30.");
                        lr = ArrayWeights.Taylor(rows, a, (int)nb);
                        lc = ArrayWeights.Taylor(cols, a, (int)nb);
                        break;
                    }
                    case "chebyshev":
                        if (!(Math.Abs(a) > 0)) throw new TypeErrorException("chebyshev(sll): sll is the sidelobe level in dB, such as -30.");
                        lr = ArrayWeights.Chebyshev(rows, a);
                        lc = ArrayWeights.Chebyshev(cols, a);
                        break;
                    default:
                        if (!(a >= 0)) throw new TypeErrorException($"cosq(q): q is a power of zero or more, not {a}.");
                        lr = ArrayWeights.CosQ(rows, a);
                        lc = ArrayWeights.CosQ(cols, a);
                        break;
                }
                return new Value(lr[row - 1] * lc[col - 1]);
            }
        }
    }

    private double Real(CallExpr cl, Expr arg, Scope scope, string what)
    {
        var v = EvalExpr(arg, scope);
        if (v.Kind != ValueKind.Real || !double.IsFinite(v.AsReal()))
            throw new TypeErrorException($"{cl.Name}(): {what} is a real number, not {v}.");
        return v.AsReal();
    }

    /// <summary>The AST the site-unit rule sees: every weight call replaced by what its RESULT is — steer and the tapers a bare number,
    /// steerDelay a value in seconds — so a unit-bearing argument (θ0 in degrees, f0 in GHz) decides nothing about the result.</summary>
    internal static Expr WeightView(Expr ast) => ast switch
    {
        CallExpr { Name: "steerDelay" } => new NumberExpr(1, "s"),
        CallExpr cl when IsWeightFunction(cl.Name) => new NumberExpr(1),
        CallExpr cl => cl with { Args = [.. cl.Args.Select(WeightView)] },
        UnaryExpr u => u with { Operand = WeightView(u.Operand) },
        BinaryExpr b => b with { Left = WeightView(b.Left), Right = WeightView(b.Right) },
        CompareExpr c => c with { Left = WeightView(c.Left), Right = WeightView(c.Right) },
        LogicExpr l => l with { Left = WeightView(l.Left), Right = WeightView(l.Right) },
        ConditionalExpr cd => new ConditionalExpr(WeightView(cd.Condition), WeightView(cd.Then), WeightView(cd.Else)),
        IndexExpr ix => ix with { Target = WeightView(ix.Target) },
        _ => ast,
    };

    /// <summary>
    /// R-paa4-2 — a phase field whose site unit is degrees (<paramref name="unit"/>, when the field was typed with an angle unit, else
    /// <c>deg</c>), in radians. An expression that carries an angle — a literal such as <c>10 deg</c>, or a VAR whose unit is an angle —
    /// takes the ordinary rule; any other is a number of degrees, whatever lengths or frequencies it holds.
    /// </summary>
    public Value EvalPhase(string expression, Scope scope, string? unit, string site)
    {
        string siteUnit = unit is { Length: > 0 } u && Units.BaseUnit(u) == "rad" ? u : "deg";
        var ast = Parse(expression);
        _statFrames.Add(new StatFrame(scope.InstancePath, site));
        try
        {
            return CarriesAngle(WeightView(NominalView(ast)), scope)
                ? EvalAtSite(ast, scope, siteUnit)
                : ApplyUnit(EvalExpr(ast, scope), siteUnit);
        }
        finally { _statFrames.RemoveAt(_statFrames.Count - 1); }
    }

    /// <summary>Whether <paramref name="ast"/> holds an angle: a literal in an angle unit, or a name whose value carries one.</summary>
    private static bool CarriesAngle(Expr ast, Scope scope)
    {
        static bool Angle(string? u) => u is { Length: > 0 } && Units.IsKnown(u) && Units.BaseUnit(u) == "rad";
        if (AnyNode(ast, e => e is NumberExpr { Unit: var u } && Angle(u))) return true;
        return AstWalker.CollectRefs(ast).Any(r => Angle(CarriedUnit(r, scope, new HashSet<string>(StringComparer.Ordinal))));
    }
}
