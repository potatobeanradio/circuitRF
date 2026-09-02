using System.Collections.Generic;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Core-layer frequency-unit helpers — the single home for Hz resolution so HB and S-param
/// resolve identically. The UI <c>FreqUnitHelper</c> delegates <c>Multiplier</c> here.
/// </summary>
public static class FreqUnit
{
    public static readonly string[] Units = ["Hz", "kHz", "MHz", "GHz"];

    /// <summary>Returns the multiplier to convert from <paramref name="unit"/> to Hz.</summary>
    public static double Multiplier(string? unit) => unit switch
    {
        "kHz" => 1e3,
        "MHz" => 1e6,
        "GHz" => 1e9,
        _     => 1.0,   // null / "Hz" / "" → ×1
    };

    /// <summary>
    /// Resolve a frequency field to Hz applying the <em>var-unit-wins</em> rule:
    /// if the expression references any variable in <paramref name="globalsWithUnit"/>, that
    /// variable was declared with an explicit unit and its resolved value is already in Hz —
    /// the field unit is ignored. Otherwise the evaluated value is multiplied by
    /// <see cref="Multiplier"/>(<paramref name="fieldUnit"/>). A pure numeric literal
    /// references no variable, so the field unit always applies.
    /// </summary>
    public static double ResolveHz(string expr, string? fieldUnit,
        IReadOnlyDictionary<string, Value> globals,
        IReadOnlyCollection<string>? globalsWithUnit)
    {
        var ast  = Parser.Parse(expr);
        double v = EvalReal(ast, globals);

        if (globalsWithUnit is { Count: > 0 })
        {
            foreach (var name in AstWalker.CollectRefs(ast))
                if (globalsWithUnit.Contains(name))
                    return v;   // var-unit-wins: resolved value is already in Hz
        }

        return v * Multiplier(fieldUnit);
    }

    /// <summary>
    /// The refusal an unresolvable frequency FIELD earns, phrased as the fact the reader needs:
    /// the field is undefined, and which name in it could not be resolved.
    ///
    /// <para>Every caller of <see cref="ResolveHz"/> used to swallow the failure and substitute
    /// 1 GHz. Nothing downstream can tell that apart from a tone the user actually typed, so the
    /// first symptom was a commensurability refusal reporting a grid the design never asked for
    /// (<c>f0=1E+09 Hz</c>) and blaming the SOURCE — while the analysis card, which does not
    /// substitute, was already saying "unknown". Fail here instead, naming the field and the name,
    /// so the run says what the card says.</para>
    /// </summary>
    /// <param name="what">The field, as the user sees it in the dialog — e.g. <c>Tone</c>, <c>Tone[2]</c>.</param>
    /// <param name="owner">Analysis name, for the "which analysis" half.</param>
    public static string UnresolvedFieldMessage(string what, string owner, string expr, System.Exception cause)
    {
        string quoted = string.IsNullOrWhiteSpace(expr) ? "(empty)" : $"\"{expr.Trim()}\"";
        string why = cause is UnresolvedNameException u
            ? $"'{u.Name}' is not a variable of this design"
            : cause.Message;
        return $"'{owner}': {what} is undefined — {what}={quoted}, and {why}. " +
               $"Define the variable, or type a frequency in {what}.";
    }

    /// <summary>
    /// Returns the power-of-1000 exponent n if |a/b| ≈ 1000ⁿ (n = 1, 2, 3, …), otherwise 0.
    /// Typical use: detect a unit-scale mismatch, e.g. 2 Hz vs 2 GHz → ratio 10⁹ = 1000³ → 3.
    /// </summary>
    public static int LooksLikeUnitMismatch(double a, double b)
    {
        if (a <= 0 || b <= 0) return 0;
        double ratio = a / b;
        if (ratio < 1) ratio = 1.0 / ratio;        // always ≥ 1
        double log10   = Math.Log10(ratio);
        int    nearest = (int)Math.Round(log10);
        if (nearest == 0 || nearest % 3 != 0) return 0;
        double expected = Math.Pow(10.0, nearest);
        if (Math.Abs(ratio - expected) / expected > 1e-3) return 0;
        return nearest / 3;   // 3 → 1 (kHz), 6 → 2 (MHz), 9 → 3 (GHz)
    }

    // Mirrors HbEngine.BuildScope / BuildEvaluator + EvalExpr. Throws on failure; callers wrap.
    private static double EvalReal(Expr ast, IReadOnlyDictionary<string, Value> globals)
    {
        var scope = new Scope("freq-resolve");
        var ev    = new Evaluator();
        foreach (var kv in globals)
        {
            scope.Bind(kv.Key, kv.Value.ToString()!);
            ev.InjectResolved("freq-resolve", kv.Key, kv.Value);
        }
        var val = ev.EvalExpr(ast, scope);
        return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
    }
}
