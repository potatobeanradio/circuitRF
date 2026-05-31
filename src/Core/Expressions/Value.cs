using System.Numerics;

namespace CircuitRF.Core.Expressions;

public enum ValueKind { Real, Complex, Bool }

/// <summary>
/// A kinded value produced by the expression engine: Real, Complex, or Bool.
/// Real and Complex map directly to the DataKind used in the result model.
/// Bool is internal to conditionals and never the final value of a parameter.
/// </summary>
public readonly struct Value
{
    public ValueKind Kind { get; }

    private readonly double   _real;
    private readonly Complex  _complex;
    private readonly bool     _bool;

    public static readonly Value Zero    = new(0.0);
    public static readonly Value One     = new(1.0);
    public static readonly Value Pi      = new(Math.PI);
    public static readonly Value E       = new(Math.E);
    public static readonly Value J       = new(Complex.ImaginaryOne);
    public static readonly Value True    = new(true);
    public static readonly Value False   = new(false);

    public Value(double real)       { Kind = ValueKind.Real;    _real = real; }
    public Value(Complex complex)   { Kind = ValueKind.Complex; _complex = complex; }
    public Value(bool b)            { Kind = ValueKind.Bool;    _bool = b; }

    public double  AsReal()    => Kind == ValueKind.Real    ? _real    : throw new InvalidCastException($"Value is {Kind}, not Real");
    public Complex AsComplex() => Kind == ValueKind.Complex ? _complex : throw new InvalidCastException($"Value is {Kind}, not Complex");
    public bool    AsBool()    => Kind == ValueKind.Bool    ? _bool    : throw new InvalidCastException($"Value is {Kind}, not Bool");

    /// <summary>Returns the value as Complex regardless of kind (promotes Real; errors on Bool).</summary>
    public Complex ToComplex() => Kind switch
    {
        ValueKind.Real    => new Complex(_real, 0),
        ValueKind.Complex => _complex,
        _                 => throw new InvalidCastException("Cannot convert Bool to Complex")
    };

    public override string ToString() => Kind switch
    {
        ValueKind.Real    => _real.ToString("G"),
        ValueKind.Complex => _complex.ToString(),
        ValueKind.Bool    => _bool.ToString(),
        _                 => "?"
    };

    // ── Promotion helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Promotes two operands to a common numeric kind for arithmetic.
    /// Returns the higher kind: if either is Complex, both become Complex.
    /// Throws if either is Bool.
    /// </summary>
    public static ValueKind CommonArithmeticKind(Value a, Value b)
    {
        if (a.Kind == ValueKind.Bool || b.Kind == ValueKind.Bool)
            throw new ExpressionException("Bool value used in arithmetic context");
        return (a.Kind == ValueKind.Complex || b.Kind == ValueKind.Complex)
            ? ValueKind.Complex
            : ValueKind.Real;
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────

    public static Value Add(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real + b._real)
            : new Value(a.ToComplex() + b.ToComplex());
    }

    public static Value Sub(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real - b._real)
            : new Value(a.ToComplex() - b.ToComplex());
    }

    public static Value Mul(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        return kind == ValueKind.Real
            ? new Value(a._real * b._real)
            : new Value(a.ToComplex() * b.ToComplex());
    }

    public static Value Div(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        if (kind == ValueKind.Real)
        {
            if (b._real == 0) throw new ExpressionException("Division by zero");
            return new Value(a._real / b._real);
        }
        var bc = b.ToComplex();
        if (bc == Complex.Zero) throw new ExpressionException("Division by zero");
        return new Value(a.ToComplex() / bc);
    }

    public static Value Pow(Value a, Value b)
    {
        var kind = CommonArithmeticKind(a, b);
        if (kind == ValueKind.Real)
        {
            // negative base with non-integer exponent → promote to Complex
            double result = Math.Pow(a._real, b._real);
            if (double.IsNaN(result) && a._real < 0)
                return new Value(Complex.Pow(new Complex(a._real, 0), new Complex(b._real, 0)));
            return new Value(result);
        }
        return new Value(Complex.Pow(a.ToComplex(), b.ToComplex()));
    }

    public static Value Negate(Value a) => a.Kind switch
    {
        ValueKind.Real    => new Value(-a._real),
        ValueKind.Complex => new Value(-a._complex),
        _                 => throw new ExpressionException("Cannot negate a Bool")
    };

    public static Value UnaryPlus(Value a) => a.Kind switch
    {
        ValueKind.Real    => a,
        ValueKind.Complex => a,
        _                 => throw new ExpressionException("Cannot apply unary + to a Bool")
    };

    // ── Comparison ───────────────────────────────────────────────────────────

    public static Value LessThan(Value a, Value b)
    {
        RequireReal(a, "<"); RequireReal(b, "<");
        return new Value(a._real < b._real);
    }

    public static Value LessOrEqual(Value a, Value b)
    {
        RequireReal(a, "<="); RequireReal(b, "<=");
        return new Value(a._real <= b._real);
    }

    public static Value GreaterThan(Value a, Value b)
    {
        RequireReal(a, ">"); RequireReal(b, ">");
        return new Value(a._real > b._real);
    }

    public static Value GreaterOrEqual(Value a, Value b)
    {
        RequireReal(a, ">="); RequireReal(b, ">=");
        return new Value(a._real >= b._real);
    }

    public static Value Equal(Value a, Value b)
    {
        if (a.Kind == ValueKind.Bool || b.Kind == ValueKind.Bool)
            throw new ExpressionException("Cannot compare Bool values with ==");
        _ = CommonArithmeticKind(a, b); // validates kinds
        return a.Kind == ValueKind.Real && b.Kind == ValueKind.Real
            ? new Value(a._real == b._real)
            : new Value(a.ToComplex() == b.ToComplex());
    }

    public static Value NotEqual(Value a, Value b)
    {
        var eq = Equal(a, b);
        return new Value(!eq._bool);
    }

    // ── Logic ────────────────────────────────────────────────────────────────

    public static Value Not(Value a)
    {
        RequireBool(a, "!");
        return new Value(!a._bool);
    }

    public static Value And(Value a, Value b)
    {
        RequireBool(a, "&&"); RequireBool(b, "&&");
        return new Value(a._bool && b._bool);
    }

    public static Value Or(Value a, Value b)
    {
        RequireBool(a, "||"); RequireBool(b, "||");
        return new Value(a._bool || b._bool);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    private static void RequireReal(Value v, string op)
    {
        if (v.Kind != ValueKind.Real)
            throw new ExpressionException($"Operator '{op}' requires real operands, got {v.Kind}");
    }

    private static void RequireBool(Value v, string op)
    {
        if (v.Kind != ValueKind.Bool)
            throw new ExpressionException($"Operator '{op}' requires Bool operands, got {v.Kind}");
    }
}
