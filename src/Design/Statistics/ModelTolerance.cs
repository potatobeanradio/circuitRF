using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A library model's tolerance, both ways (brief vendorlib-13 R-vl13-1b): a spread typed in the Yield panel's own
/// grammar (<see cref="ToleranceText"/>) written as the distribution function on a parameter's default, and such a
/// function read back into that grammar for the Export dialog's Tolerance column and the consumer's Library parts
/// group. The arithmetic of a draw stays the expression engine's (<c>Evaluator.Statistical</c>); this only moves text.
///
/// <para><b>The mapping.</b> Gaussian <c>± x %</c> at kσ → <c>gauss(nom,x/100,k)</c>; Gaussian <c>σ a</c> →
/// <c>agauss(nom,a,1)</c>; Gaussian <c>± a</c> at kσ → <c>agauss(nom,a,k)</c>; Uniform <c>± x %</c> →
/// <c>unif(nom,x/100)</c>; Uniform <c>± a</c> → <c>aunif(nom,a)</c>; Uniform <c>a … b</c> →
/// <c>aunif((a+b)/2,(b−a)/2)</c>, computed. An absolute width is written in the parameter's own unit, which is the unit
/// the whole default is read in. Lognormal, Discrete and truncation have no distribution function and are refused.
/// The text carries no spaces, so it survives a <c>parameters</c> line as one value however it is read.</para>
/// </summary>
public static partial class ModelTolerance
{
    /// <summary>The distributions a model's tolerance can be written with — the two that have a function.</summary>
    public static readonly IReadOnlyList<StatDistribution> Offered = [StatDistribution.Gauss, StatDistribution.Unif];

    /// <summary>Why the others are not offered — the Tolerance column's tooltip.</summary>
    public const string NotOfferedReason =
        "A model's tolerance is written as a distribution function in the parameter's default, and only Gaussian and " +
        "Uniform have one: Lognormal, Discrete and truncation cannot be written that way, so they are not offered here.";

    [GeneratedRegex(@"^\s*(?<f>agauss|gauss|aunif|unif|limit)\s*\((?<a>.*)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CallShape();

    /// <summary>The nominal of a default: its first argument when it is a distribution call, else the text itself.</summary>
    public static string NominalOf(string expression)
        => Split(expression) is { } c && c.Args.Count > 0 ? c.Args[0] : expression.Trim();

    /// <summary>
    /// The default that carries <paramref name="spread"/> on <paramref name="nominal"/> as a <paramref name="d"/> draw,
    /// or null with the reason. <paramref name="nominal"/> may itself be a distribution — its nominal is used.
    /// </summary>
    public static string? ToFunction(string nominal, string? parameterUnit, StatDistribution d, StatSpread spread, out string? refusal)
    {
        refusal = null;
        string nom = NominalOf(nominal);
        if (nom.Length == 0) { refusal = "the parameter has no nominal value to spread around"; return null; }
        if (!Offered.Contains(d)) { refusal = NotOfferedReason; return null; }
        if (spread.Trunc is not null) { refusal = "a truncation cannot be written as a distribution function"; return null; }
        if (ToleranceText.WrongShape(spread, d) is { } shape) { refusal = shape; return null; }

        if (d == StatDistribution.Gauss)
        {
            if (spread.Sd is { } sd)
                return Number(sd, parameterUnit, out refusal) is { } a ? $"agauss({nom},{a},1)" : null;
            if (spread.Tol is not { } tol) { refusal = "write ± 5 % at 3σ or σ 1 Ω"; return null; }
            string k = spread.Sigmas ?? ToleranceText.DefaultSigmas;
            if (!double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out double kv) || !(kv > 0))
            { refusal = $"'{k}' is not a sigma count above zero"; return null; }
            if (StatSpread.IsPercent(tol))
                return Fraction(tol, out refusal) is { } rel ? $"gauss({nom},{rel},{Show(kv)})" : null;
            return Number(tol, parameterUnit, out refusal) is { } dev ? $"agauss({nom},{dev},{Show(kv)})" : null;
        }

        // Uniform.
        if (spread.Tol is { } u)
        {
            if (StatSpread.IsPercent(u))
                return Fraction(u, out refusal) is { } rel ? $"unif({nom},{rel})" : null;
            return Number(u, parameterUnit, out refusal) is { } dev ? $"aunif({nom},{dev})" : null;
        }
        if (spread.Lo is { } lo && spread.Hi is { } hi)
        {
            if (StatSpread.IsPercent(lo) || StatSpread.IsPercent(hi))
            { refusal = "a range is written in values, like 45 … 55 Ω"; return null; }
            if (Value(lo, parameterUnit, out refusal) is not { } a || Value(hi, parameterUnit, out refusal) is not { } b) return null;
            if (!(b > a)) { refusal = "the range's upper end must be above its lower end"; return null; }
            return $"aunif({Show((a + b) / 2)},{Show((b - a) / 2)})";
        }
        refusal = "write ± 5 % or 45 … 55 Ω";
        return null;
    }

    /// <summary>A model's distribution read back: its nominal, its distribution and its spread in the panel's own keys —
    /// or null when <paramref name="expression"/> is not a distribution call this grammar can show.</summary>
    public static (string Nominal, StatDistribution Distribution, StatSpread Spread)? FromFunction(string expression, string? parameterUnit)
    {
        if (Split(expression) is not { } c || c.Args.Count < 2) return null;
        string nom = c.Args[0];
        string unit = string.IsNullOrWhiteSpace(parameterUnit) ? "" : " " + parameterUnit.Trim();
        double? A(int i) => i < c.Args.Count && double.TryParse(c.Args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        switch (c.Function)
        {
            case "gauss" when A(1) is { } rel:
                return (nom, StatDistribution.Gauss, new StatSpread { Tol = Show(rel * 100) + "%", Sigmas = Show(A(2) ?? 1) });
            case "agauss" when A(1) is { } dev:
                double k = A(2) ?? 1;
                return k == 1
                    ? (nom, StatDistribution.Gauss, new StatSpread { Sd = Show(dev) + unit })
                    : (nom, StatDistribution.Gauss, new StatSpread { Tol = Show(dev) + unit, Sigmas = Show(k) });
            case "unif" when A(1) is { } rel:
                return (nom, StatDistribution.Unif, new StatSpread { Tol = Show(rel * 100) + "%" });
            case "aunif" when A(1) is { } half:
                return A(0) is { } mid
                    ? (nom, StatDistribution.Unif, new StatSpread { Lo = Show(mid - half) + unit, Hi = Show(mid + half) + unit })
                    : (nom, StatDistribution.Unif, new StatSpread { Tol = Show(half) + unit });
            default:
                return null;
        }
    }

    /// <summary>The spread as the panel shows it (<c>± 5 % at 3σ</c>), or the expression itself when it is a
    /// distribution the grammar cannot show (a <c>limit</c>, a spread written as an expression).</summary>
    public static string Describe(string expression, string? parameterUnit)
        => FromFunction(expression, parameterUnit) is { } t ? ToleranceText.Format(t.Distribution, t.Spread) : expression.Trim();

    // ── text ────────────────────────────────────────────────────────────────────

    private sealed record Call(string Function, IReadOnlyList<string> Args);

    /// <summary>A top-level distribution call split into its arguments at depth-zero commas; null for anything else.</summary>
    private static Call? Split(string expression)
    {
        var m = CallShape().Match(expression);
        if (!m.Success) return null;
        string body = m.Groups["a"].Value;
        var args = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char ch = body[i];
            if (ch == '(') depth++;
            else if (ch == ')') { if (--depth < 0) return null; }
            else if (ch == ',' && depth == 0) { args.Add(body[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0) return null;
        args.Add(body[start..].Trim());
        return new Call(m.Groups["f"].Value, args);
    }

    /// <summary><c>5%</c> → <c>0.05</c>, exactly (decimal arithmetic, so 7 % is 0.07 and not 0.07000000000000001).</summary>
    private static string? Fraction(string percent, out string? refusal)
    {
        refusal = null;
        string t = percent.Trim()[..^1].Trim();
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal p) && p > 0)
            return (p / 100m).ToString(CultureInfo.InvariantCulture);
        refusal = $"'{percent}' is not a percent above zero";
        return null;
    }

    /// <summary>An absolute width as a number in the parameter's unit, written for the function.</summary>
    private static string? Number(string text, string? parameterUnit, out string? refusal)
    {
        if (Value(text, parameterUnit, out refusal) is not { } v) return null;
        if (!(v > 0)) { refusal = $"'{text}' is not a width above zero"; return null; }
        return Show(v);
    }

    /// <summary><c>0.1 pF</c> in a pF parameter → 0.1; in an F parameter → 1e-13; a bare number is in the parameter's unit.</summary>
    private static double? Value(string text, string? parameterUnit, out string? refusal)
    {
        refusal = null;
        string t = text.Trim();
        int space = t.IndexOf(' ');
        string n = space < 0 ? t : t[..space];
        string? u = space < 0 ? null : UnitNormalizer.ToEngineUnit(t[(space + 1)..].Trim());
        if (!double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        { refusal = $"'{text}' is not a number"; return null; }
        if (u is null) return v;
        string pu = UnitNormalizer.ToEngineUnit(parameterUnit ?? "");
        if (Units.Scale(u) is not { } su) { refusal = $"'{u}' is not a unit"; return null; }
        double sp = pu.Length == 0 ? 1.0 : Units.Scale(pu) ?? 1.0;
        if (pu.Length > 0 && Units.BaseUnit(u) != Units.BaseUnit(pu))
        { refusal = $"'{text}' is not in the parameter's unit ({parameterUnit})"; return null; }
        return v * su / sp;
    }

    private static string Show(double v) => v.ToString("G15", CultureInfo.InvariantCulture);
}
