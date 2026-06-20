using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CircuitRF.Core.Expressions;

// Inline gradient storage — allocation-free, stack-friendly.
// MaxN = 16 covers portCount + controlCount (up to 8 ports + 8 control refs).
[InlineArray(Dual.MaxN)]
internal struct DualGrad { private double _e; }

/// <summary>
/// Forward-mode dual number for automatic differentiation.
/// Carries a value and an N-wide gradient (N = port count + control count, ≤ MaxN).
/// Entirely a value type — no heap allocation per operation (§2.6).
/// </summary>
public struct Dual : IAdScalar<Dual>
{
    public const int MaxN = 16;

    // Overflow cap for Exp. exp(709) ≈ 8.2e307 < double.MaxValue; cap at 700 to stay clear.
    private const double ExpCap = 700.0;
    // Floor for Log/Sqrt domain clamp.
    private const double LogFloor = 1e-300;

    public double Value;
    private DualGrad _grad;
    public int N;

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>Create a constant (parameter) with zero gradient.</summary>
    public static Dual Param(double value, int n)
    {
        var d = new Dual { Value = value, N = n };
        return d;
    }

    /// <summary>
    /// Create a port-voltage seed: value v, gradient 1 in slot <paramref name="slot"/>,
    /// 0 elsewhere. Seeds the independent variable for forward-mode AD.
    /// </summary>
    public static Dual Seed(double value, int n, int slot)
    {
        var d = new Dual { Value = value, N = n };
        d._grad[slot] = 1.0;
        return d;
    }

    public double GetGrad(int i) => _grad[i];

    // ── IAdScalar<Dual> ──────────────────────────────────────────────────────

    public static Dual Constant(double d) => Param(d, 0);  // N set by caller context; 0 = parameter
    public static double ValueOf(Dual a) => a.Value;

    public static Dual Add(Dual a, Dual b)
    {
        var r = new Dual { Value = a.Value + b.Value, N = NMax(a, b) };
        int n = r.N;
        for (int i = 0; i < n; i++) r._grad[i] = a._grad[i] + b._grad[i];
        return r;
    }

    public static Dual Sub(Dual a, Dual b)
    {
        var r = new Dual { Value = a.Value - b.Value, N = NMax(a, b) };
        int n = r.N;
        for (int i = 0; i < n; i++) r._grad[i] = a._grad[i] - b._grad[i];
        return r;
    }

    public static Dual Mul(Dual a, Dual b)
    {
        var r = new Dual { Value = a.Value * b.Value, N = NMax(a, b) };
        int n = r.N;
        for (int i = 0; i < n; i++) r._grad[i] = a._grad[i] * b.Value + a.Value * b._grad[i];
        return r;
    }

    public static Dual Div(Dual a, Dual b)
    {
        // (a/b)' = (a'b - ab') / b^2
        double bv = b.Value;
        double bv2 = bv * bv;
        var r = new Dual { Value = a.Value / bv, N = NMax(a, b) };
        int n = r.N;
        for (int i = 0; i < n; i++)
            r._grad[i] = (a._grad[i] * bv - a.Value * b._grad[i]) / bv2;
        return r;
    }

    public static Dual Neg(Dual a)
    {
        var r = new Dual { Value = -a.Value, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = -a._grad[i];
        return r;
    }

    // (a^b)' — full formula: a^b * (b/a * a' + ln(a) * b')
    // When b is a constant (b.Grad all zero): simplifies to b*a^(b-1)*a'.
    public static Dual Pow(Dual a, Dual b)
    {
        double av = a.Value, bv = b.Value;
        double pval = Math.Pow(av, bv);
        var r = new Dual { Value = pval, N = NMax(a, b) };
        int n = r.N;
        // b/a factor (for a' term): bv/av, guarded against av=0
        double bOverA = av != 0.0 ? bv / av : 0.0;
        // ln(a) factor (for b' term)
        double lnA = av > 0.0 ? Math.Log(av) : 0.0;
        for (int i = 0; i < n; i++)
            r._grad[i] = pval * (bOverA * a._grad[i] + lnA * b._grad[i]);
        return r;
    }

    // Overflow-safe exp: cap argument at ExpCap. Prevents NaN cascade on overshoot iterates.
    // Gradient uses the capped value — at the cap edge the gradient is also capped,
    // which is correct for the softplus pattern log(exp(x)+1): both value and gradient
    // stay finite and recover the correct softplus when composed with Log.
    public static Dual Exp(Dual a)
    {
        double xv = a.Value > ExpCap ? ExpCap : a.Value;
        double ev = Math.Exp(xv);
        var r = new Dual { Value = ev, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = ev * a._grad[i];
        return r;
    }

    // Domain-safe log: clamps to LogFloor and warns (§2.5).
    public static Dual Log(Dual a)
    {
        double av = a.Value;
        if (av <= 0.0)
        {
            AdWarnings.WarnDomain("log", av);
            av = LogFloor;
        }
        var r = new Dual { Value = Math.Log(av), N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = a._grad[i] / av;
        return r;
    }

    // Domain-safe sqrt: clamps to 0 and warns.
    public static Dual Sqrt(Dual a)
    {
        double av = a.Value;
        if (av < 0.0)
        {
            AdWarnings.WarnDomain("sqrt", av);
            av = 0.0;
        }
        double sv = Math.Sqrt(av);
        double denom = sv > 0.0 ? 2.0 * sv : 1e-150;  // avoid /0 at exactly 0
        var r = new Dual { Value = sv, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = a._grad[i] / denom;
        return r;
    }

    public static Dual Tanh(Dual a)
    {
        double tv = Math.Tanh(a.Value);
        double dt = 1.0 - tv * tv;  // sech²
        var r = new Dual { Value = tv, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = dt * a._grad[i];
        return r;
    }

    public static Dual Sin(Dual a)
    {
        double sv = Math.Sin(a.Value);
        double cv = Math.Cos(a.Value);
        var r = new Dual { Value = sv, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = cv * a._grad[i];
        return r;
    }

    public static Dual Cos(Dual a)
    {
        double cv = Math.Cos(a.Value);
        double sv = Math.Sin(a.Value);
        var r = new Dual { Value = cv, N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = -sv * a._grad[i];
        return r;
    }

    // abs: sub-gradient at 0 is +1 (documented; measure-zero kink, no effect on Newton).
    public static Dual Abs(Dual a)
    {
        double sign = a.Value >= 0.0 ? 1.0 : -1.0;
        var r = new Dual { Value = Math.Abs(a.Value), N = a.N };
        for (int i = 0; i < a.N; i++) r._grad[i] = sign * a._grad[i];
        return r;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // N for a binary result: prefer the non-zero N (one operand may be a plain constant with N=0).
    private static int NMax(Dual a, Dual b) => a.N >= b.N ? a.N : b.N;

    public override string ToString()
    {
        var self = this;
        return $"Dual({Value:G6}, [{string.Join(", ", Enumerable.Range(0, self.N).Select(i => self._grad[i].ToString("G4")))}])";
    }
}
