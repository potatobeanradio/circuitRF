namespace CircuitRF.Core.Expressions;

/// <summary>
/// Thin double wrapper implementing IAdScalar for the plain-value (FD/double) SDD path.
/// Uses the same domain guards as Dual so FD cross-checks behave identically.
/// </summary>
public readonly struct SddScalar(double value) : IAdScalar<SddScalar>
{
    public readonly double Value = value;

    private const double ExpCap  = 700.0;
    private const double LogFloor = 1e-300;

    public static SddScalar Constant(double d) => new(d);
    public static double ValueOf(in SddScalar a) => a.Value;

    public static SddScalar Add(in SddScalar a, in SddScalar b) => new(a.Value + b.Value);
    public static SddScalar Sub(in SddScalar a, in SddScalar b) => new(a.Value - b.Value);
    public static SddScalar Mul(in SddScalar a, in SddScalar b) => new(a.Value * b.Value);
    public static SddScalar Div(in SddScalar a, in SddScalar b) => new(a.Value / b.Value);
    public static SddScalar Neg(in SddScalar a) => new(-a.Value);
    public static SddScalar Pow(in SddScalar a, in SddScalar b) => new(Math.Pow(a.Value, b.Value));

    public static SddScalar Exp(in SddScalar a)
    {
        double xv = a.Value > ExpCap ? ExpCap : a.Value;
        return new(Math.Exp(xv));
    }

    public static SddScalar Log(in SddScalar a)
    {
        double av = a.Value;
        if (av <= 0.0)
        {
            AdWarnings.WarnDomain("log", av);
            av = LogFloor;
        }
        return new(Math.Log(av));
    }

    public static SddScalar Sqrt(in SddScalar a)
    {
        double av = a.Value;
        if (av < 0.0)
        {
            AdWarnings.WarnDomain("sqrt", av);
            av = 0.0;
        }
        return new(Math.Sqrt(av));
    }

    public static SddScalar Tanh(in SddScalar a) => new(Math.Tanh(a.Value));
    public static SddScalar Sin(in SddScalar a)  => new(Math.Sin(a.Value));
    public static SddScalar Cos(in SddScalar a)  => new(Math.Cos(a.Value));
    public static SddScalar Abs(in SddScalar a)  => new(Math.Abs(a.Value));

    public override string ToString() => Value.ToString("G");
}
