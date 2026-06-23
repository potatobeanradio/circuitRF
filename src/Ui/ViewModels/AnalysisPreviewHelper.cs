using System;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Shared expression-preview helper for analysis-editor fields.
/// Mirrors the pattern in <see cref="ParameterRowViewModel"/> exactly:
/// <c>DesignScope.Build + new Evaluator().Eval</c>, swallow all errors → empty string,
/// bare number or blank → empty string (no "≈ 2.5" noise on a plain literal).
/// </summary>
internal static class AnalysisPreviewHelper
{
    public static string ComputePreview(string expression, SchematicEditModel model)
    {
        string trimmed = expression.Trim();
        if (trimmed.Length == 0) return "";
        if (IsBareNumber(trimmed)) return "";

        try
        {
            var scope = DesignScope.Build(model);
            var value = new Evaluator().Eval(trimmed, scope);
            return value.Kind switch
            {
                ValueKind.Real    => "≈ " + FormatReal(value.AsReal()),
                ValueKind.Complex => "≈ " + FormatComplex(value.AsComplex()),
                _                 => "",
            };
        }
        catch (UnresolvedNameException unresolved)
        {
            return $"≈ unknown: {unresolved.Name}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Frequency-field preview that mirrors the engine's var-unit-wins rule.
    /// Evaluates the raw coefficient against DesignScope (units stripped), then applies the unit
    /// of the first referenced variable that declares a frequency unit; if none, applies the field
    /// unit. Returns "≈ &lt;Hz&gt;" / "≈ unknown: X" / "" exactly like ComputePreview.
    /// Known approximation: for a mixed-unit compound expression (e.g. RFfreq + Voff) the
    /// single-multiplier result is approximate — consistent with the engine's own var-unit-wins
    /// approximation. Homogeneous cases (bare reference, 2*RFfreq) are exact.
    /// </summary>
    public static string ComputeFreqPreview(string coeff, string fieldUnit, SchematicEditModel model)
    {
        string expr = coeff.Trim();
        if (expr.Length == 0) return "";

        // Bare-number + Hz: suppress (no "≈ 2.4" noise on a plain Hz literal).
        // Bare-number + other unit: show preview (confirms the multiplier applied).
        if (IsBareNumber(expr) && fieldUnit == "Hz") return "";

        try
        {
            var ast   = Parser.Parse(expr);
            var scope = DesignScope.Build(model);
            double v  = new Evaluator().Eval(expr, scope).AsReal();

            // var-unit-wins: first referenced name whose model param carries a non-empty unit.
            string? refUnit = null;
            foreach (var name in AstWalker.CollectRefs(ast))
            {
                string? u = LookupParamUnit(model, name);
                if (!string.IsNullOrEmpty(u)) { refUnit = u; break; }
            }

            double hz = v * FreqUnit.Multiplier(refUnit ?? fieldUnit);
            return "≈ " + FormatReal(hz);
        }
        catch (UnresolvedNameException ex) { return $"≈ unknown: {ex.Name}"; }
        catch { return ""; }
    }

    private static string? LookupParamUnit(SchematicEditModel model, string name)
    {
        foreach (var c in model.Components)
            foreach (var p in c.Parameters)
                if (p.Name == name && !string.IsNullOrEmpty(p.Unit))
                    return p.Unit;
        return null;
    }

    private static bool IsBareNumber(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                           CultureInfo.InvariantCulture, out _);

    private static string FormatReal(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
            return v.ToString(CultureInfo.InvariantCulture);
        return v.ToString("G4", CultureInfo.InvariantCulture);
    }

    private static string FormatComplex(Complex c)
    {
        string re   = FormatReal(c.Real);
        string im   = FormatReal(Math.Abs(c.Imaginary));
        string sign = c.Imaginary < 0 ? "-" : "+";
        return $"{re} {sign} {im}j";
    }
}
