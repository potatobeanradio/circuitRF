using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A statistical entry's spread in numbers (docs/design/yield.md §2): the written values turned into
/// base SI against the value's nominal. A percent is a percent OF the nominal — <c>sd=2%</c> of 50 Ω
/// is 1 Ω, <c>lo=90%</c> is 45 Ω — and a value with no unit is in the parameter's own unit.
/// </summary>
/// <param name="NominalSi">The nominal, base SI.</param>
/// <param name="Unit">The parameter's own unit (engine spelling, "" for none).</param>
/// <param name="Sigma">gauss, lognorm: 1σ, base SI. For lognorm, σ/nominal is the log's σ.</param>
/// <param name="Lo">unif, discrete: the lower end, base SI.</param>
/// <param name="Hi">unif, discrete: the upper end, base SI.</param>
/// <param name="Step">discrete: the step, base SI.</param>
/// <param name="Trunc">gauss, lognorm: the truncation in σ; null for none.</param>
public sealed record ResolvedSpread(
    StatDistribution Distribution,
    double  NominalSi,
    string  Unit,
    double? Sigma,
    double? Lo,
    double? Hi,
    double? Step,
    double? Trunc)
{
    /// <summary>The spread of <paramref name="e"/> around <paramref name="t"/>'s value, or null when a
    /// key the distribution needs is missing or not a number — which <see cref="StatisticsValidator"/>
    /// reports.</summary>
    public static ResolvedSpread? Of(TunableEntry e, Tunable t)
    {
        var sp = e.Spread ?? new StatSpread();
        double scale = t.Unit.Length == 0 ? 1 : Units.Scale(t.Unit) ?? 1;
        double nominal = t.Value * scale;
        double? trunc = Number(sp.Trunc);
        string? tolText = TolText(sp, t);

        switch (e.Distribution)
        {
            case StatDistribution.Gauss:
            case StatDistribution.LogNorm:
            {
                double? sigma = sp.Sd is not null ? Width(sp.Sd, nominal, t.Unit)
                              : Width(tolText, nominal, t.Unit) is { } tol && Number(sp.Sigmas) is { } k && k != 0 ? tol / k
                              : null;
                return sigma is null ? null : new(e.Distribution, nominal, t.Unit, sigma, null, null, null, trunc);
            }
            case StatDistribution.Unif:
            {
                if (sp.Tol is not null)
                    return Width(tolText, nominal, t.Unit) is { } tol
                        ? new(e.Distribution, nominal, t.Unit, null, nominal - tol, nominal + tol, null, null) : null;
                return Point(sp.Lo, nominal, t.Unit) is { } lo && Point(sp.Hi, nominal, t.Unit) is { } hi
                    ? new(e.Distribution, nominal, t.Unit, null, lo, hi, null, null) : null;
            }
            case StatDistribution.Discrete:
                return Point(sp.Lo, nominal, t.Unit) is { } dlo && Point(sp.Hi, nominal, t.Unit) is { } dhi
                       && Width(sp.By, nominal, t.Unit) is { } by
                    ? new(e.Distribution, nominal, t.Unit, null, dlo, dhi, by, null) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The probability a draw is ≤ 0 — non-physical for a value that must be positive (yield overview
    /// D2). A truncated normal is renormalized over its ±k σ window; a log-normal never draws one.
    /// <paramref name="sigmaScale"/> widens the spread exactly as <see cref="Marginal"/> does, so the answer is
    /// about the distribution a trial draws from.
    /// </summary>
    public double NonPhysicalProbability(double sigmaScale = 1)
    {
        switch (Distribution)
        {
            case StatDistribution.Gauss when Sigma is > 0 && sigmaScale > 0:
            {
                double z = NominalSi / (Sigma.Value * sigmaScale);   // how many σ from the mean zero sits
                if (Trunc is not { } k || k <= 0) return SpecialFunctions.NormalCdf(-z);
                if (z >= k) return 0;
                double tail = SpecialFunctions.NormalCdf(-k);
                return (SpecialFunctions.NormalCdf(-z) - tail) / (1 - 2 * tail);
            }
            case StatDistribution.Unif when Lo is { } ulo && Hi is { } uhi && uhi > ulo:
            {
                double mid = (ulo + uhi) / 2, half = (uhi - ulo) / 2 * sigmaScale;
                double lo = mid - half, hi = mid + half;
                return lo >= 0 ? 0 : (Math.Min(0, hi) - lo) / (hi - lo);
            }
            case StatDistribution.Discrete when Lo is { } dlo && Hi is { } dhi && Step is > 0:
            {
                int n = (int)Math.Floor((dhi - dlo) / Step.Value + 1e-9) + 1;
                int bad = 0;
                for (int i = 0; i < n; i++) if (dlo + i * Step.Value <= 0) bad++;
                return n <= 0 ? 0 : (double)bad / n;
            }
            default:
                return 0;
        }
    }

    /// <summary>
    /// The distribution a trial draws from, in base SI (docs/design/yield.md §6), with
    /// <paramref name="sigmaScale"/> multiplying the spread: a Gaussian's and a log-normal's σ, and a
    /// uniform's half-width about its centre. A discrete list is not scaled — its values are the legal
    /// ones. Null when the spread is incomplete, or for a log-normal of a nominal ≤ 0, which no
    /// log-normal can describe.
    /// </summary>
    public Marginal? Marginal(double sigmaScale = 1)
    {
        switch (Distribution)
        {
            case StatDistribution.Gauss when Sigma is { } s:
                return new NormalMarginal(NominalSi, s * sigmaScale, Trunc);
            case StatDistribution.LogNorm when Sigma is { } s && NominalSi > 0:
                return new LogNormalMarginal(NominalSi, s * sigmaScale / NominalSi, Trunc);
            case StatDistribution.Unif when Lo is { } lo && Hi is { } hi:
            {
                double mid = (lo + hi) / 2, half = (hi - lo) / 2 * sigmaScale;
                return new UniformMarginal(mid - half, mid + half);
            }
            case StatDistribution.Discrete when Lo is { } dlo && Hi is { } dhi && Step is > 0:
                return new DiscreteMarginal(dlo, dhi, Step.Value);
            default:
                return null;
        }
    }

    /// <summary>
    /// The <c>tol</c> an entry draws with: as written, or for <c>tol=part</c> the vendor part's own tolerance as the
    /// spread text an explicit one would be (<c>0.1 pF</c>, <c>5%</c>) — null when the part's is not known, which
    /// <see cref="StatisticsValidator"/> reports. It follows the part: <paramref name="t"/> at a moved design carries
    /// the candidate its part is bound to (brief vendorlib-5 R-vl5-3b/3d).
    /// </summary>
    public static string? TolText(StatSpread sp, Tunable t)
        => sp.TolIsPart ? t.PartTolerance?.SpreadText : sp.Tol;

    /// <summary>A spread width (sd, tol, by) in base SI: a percent of |nominal|, or a value.</summary>
    internal static double? Width(string? text, double nominal, string unit)
        => text is null ? null
         : StatSpread.Percent(text) is { } p ? Math.Abs(nominal) * p / 100
         : Absolute(text, unit);

    /// <summary>A spread END (lo, hi) in base SI: a percent of the nominal, or a value.</summary>
    internal static double? Point(string? text, double nominal, string unit)
        => text is null ? null
         : StatSpread.Percent(text) is { } p ? nominal * p / 100
         : Absolute(text, unit);

    /// <summary>
    /// Whether <paramref name="text"/> carries a unit of a different quantity from <paramref name="unit"/>, the
    /// parameter's own — <c>3 pF</c> for a resistance. A percent, a bare number and a bare SI prefix (<c>2 k</c>) carry
    /// none, and a parameter with no unit of its own accepts any.
    /// </summary>
    internal static bool ForeignUnit(string text, string unit)
    {
        if (unit.Length == 0 || StatSpread.Percent(text) is not null) return false;
        if (!TunableValue.TryParse(text, out _, out string written, out _) || written.Length == 0) return false;
        return Quantity(written) is { } q && q != Quantity(unit);
    }

    /// <summary>A unit's quantity — its base unit, spelled one way (<c>ohm</c> is <c>Ohm</c>); null for a bare prefix.</summary>
    private static string? Quantity(string unit)
    {
        string b = Units.BaseUnit(unit);
        if (b == unit && unit.Length == 1 && Units.Scale(unit) is { } s && s != 1) return null;
        if (b is "deg" or "rad") return "angle";
        return b.Equals("Ohm", StringComparison.OrdinalIgnoreCase) ? "Ohm" : b;
    }

    private static double? Absolute(string text, string unit)
    {
        if (TunableValue.InUnit(text, unit) is not { } n) return null;
        return n * (unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1);
    }

    internal static double? Number(string? text)
        => text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
