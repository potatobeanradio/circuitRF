namespace CircuitRF.Core.Devices.Mos;

/// <summary>
/// A value carried together with its EXACT partial derivatives with respect to the three bias
/// variables a MOS channel law is written in — <c>Vgs</c>, <c>Vds</c> and <c>Vbs</c>.
///
/// <para><b>Why this exists, when every other model in this directory differentiates by hand.</b>
/// The level-1 law is four lines and its three derivatives are four more. The level-3 law is a
/// dozen stages deep — a short-channel factor inside a threshold, inside a bulk-charge factor,
/// inside a saturation voltage, inside a velocity-saturation denominator, inside a
/// channel-length-modulation multiplier — and the chain rule through that is several hundred lines
/// of algebra in which a single wrong term produces a Jacobian that is plausible everywhere and
/// right nowhere.</para>
///
/// <para><b>This is not a finite difference and not an approximation.</b> Each operator applies the
/// product, quotient or chain rule exactly, so what comes out the far end is the analytic
/// derivative — the same number a correct hand derivation would give, obtained in a way that cannot
/// drop a term. The repo already carries dual numbers for exactly this reason in the SDD's own
/// evaluator; this is the same idea with a fixed arity, which is what makes it allocation-free.</para>
///
/// <para>A readonly struct of four doubles, with every operator inlined by construction: it lives on
/// the Newton inner loop, and the alternative it replaces is not "faster hand-written code" but
/// "hand-written code with a term missing in it".</para>
/// </summary>
internal readonly struct Grad3(double v, double dGs, double dDs, double dBs)
{
    /// <summary>The value.</summary>
    public readonly double V = v;

    /// <summary>∂value/∂Vgs.</summary>
    public readonly double DGs = dGs;

    /// <summary>∂value/∂Vds.</summary>
    public readonly double DDs = dDs;

    /// <summary>∂value/∂Vbs.</summary>
    public readonly double DBs = dBs;

    /// <summary>A constant — a value whose derivatives are all zero.</summary>
    public static Grad3 Const(double value) => new(value, 0, 0, 0);

    /// <summary>The independent variable Vgs.</summary>
    public static Grad3 Vgs(double value) => new(value, 1, 0, 0);

    /// <summary>The independent variable Vds.</summary>
    public static Grad3 Vds(double value) => new(value, 0, 1, 0);

    /// <summary>The independent variable Vbs.</summary>
    public static Grad3 Vbs(double value) => new(value, 0, 0, 1);

    public static Grad3 operator +(Grad3 a, Grad3 b)
        => new(a.V + b.V, a.DGs + b.DGs, a.DDs + b.DDs, a.DBs + b.DBs);

    public static Grad3 operator +(Grad3 a, double k) => new(a.V + k, a.DGs, a.DDs, a.DBs);
    public static Grad3 operator +(double k, Grad3 a) => a + k;

    public static Grad3 operator -(Grad3 a, Grad3 b)
        => new(a.V - b.V, a.DGs - b.DGs, a.DDs - b.DDs, a.DBs - b.DBs);

    public static Grad3 operator -(Grad3 a, double k) => new(a.V - k, a.DGs, a.DDs, a.DBs);
    public static Grad3 operator -(double k, Grad3 a) => new(k - a.V, -a.DGs, -a.DDs, -a.DBs);
    public static Grad3 operator -(Grad3 a) => new(-a.V, -a.DGs, -a.DDs, -a.DBs);

    // Product rule.
    public static Grad3 operator *(Grad3 a, Grad3 b)
        => new(a.V * b.V,
               a.DGs * b.V + a.V * b.DGs,
               a.DDs * b.V + a.V * b.DDs,
               a.DBs * b.V + a.V * b.DBs);

    public static Grad3 operator *(Grad3 a, double k) => new(a.V * k, a.DGs * k, a.DDs * k, a.DBs * k);
    public static Grad3 operator *(double k, Grad3 a) => a * k;

    // Quotient rule.
    public static Grad3 operator /(Grad3 a, Grad3 b)
    {
        double inv = 1.0 / b.V, inv2 = inv * inv;
        return new(a.V * inv,
                   (a.DGs * b.V - a.V * b.DGs) * inv2,
                   (a.DDs * b.V - a.V * b.DDs) * inv2,
                   (a.DBs * b.V - a.V * b.DBs) * inv2);
    }

    public static Grad3 operator /(Grad3 a, double k) => a * (1.0 / k);

    public static Grad3 operator /(double k, Grad3 b)
    {
        double inv = 1.0 / b.V, f = -k * inv * inv;
        return new(k * inv, f * b.DGs, f * b.DDs, f * b.DBs);
    }

    /// <summary>√x, with the chain rule. The caller is responsible for keeping the argument
    /// positive — a square root's derivative is unbounded at zero, and every use here floors its
    /// own argument for that reason rather than being guarded generically.</summary>
    public static Grad3 Sqrt(Grad3 a)
    {
        double r = System.Math.Sqrt(a.V);
        double f = 0.5 / r;
        return new(r, f * a.DGs, f * a.DDs, f * a.DBs);
    }

    /// <summary>x², written out rather than as <c>a * a</c> so the intent reads at the call site.</summary>
    public static Grad3 Sq(Grad3 a) => a * a;
}
