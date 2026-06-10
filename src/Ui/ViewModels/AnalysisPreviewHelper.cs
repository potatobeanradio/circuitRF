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
