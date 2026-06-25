using System;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Shared expression-preview helper for the Add/Edit Analysis dialog fields (analysis-authoring.md
/// §4.3; full treatment in docs/design/expressions.md "Design-time value preview").
///
/// Resolves a field expression against a design-time mirror of the schematic's variables
/// (<see cref="DesignScope.BuildResolved"/> — units bound) and shows the resulting value with an
/// HONEST prefix:
///   • "= value" when the displayed digits reconstruct the value exactly (to ~1e-12 relative — i.e.
///     ignoring floating-point dust, but flagging genuine display rounding).
///   • "≈ value" when the value had to be rounded to fit the display budget.
///
/// Scope &amp; limits: the preview scope is a FLAT design-time mirror. It binds every named parameter
/// expression (with its unit) but does NOT model hierarchy, per-instance parameter overrides,
/// sweeps, or post-run measurement context (HB1.V(...)). An expression that depends on those
/// resolves only approximately, or not at all (→ no preview). A bare literal and an unresolved name
/// never show "=".
/// </summary>
internal static class AnalysisPreviewHelper
{
    /// <summary>
    /// Preview for a general (non-frequency) expression field. Empty for blank / bare-number /
    /// unresolvable input (no "= 2.5" noise on a plain literal).
    /// </summary>
    public static string ComputePreview(string expression, SchematicEditModel model)
    {
        string trimmed = expression.Trim();
        if (trimmed.Length == 0) return "";
        if (IsBareNumber(trimmed)) return "";

        try
        {
            var scope = DesignScope.BuildResolved(model);
            var value = new Evaluator().Eval(trimmed, scope);
            return value.Kind switch
            {
                ValueKind.Real    => Prefixed(FormatRealHonest(value.AsReal())),
                ValueKind.Complex => PrefixedComplex(value.AsComplex()),
                _                 => "",
            };
        }
        catch (UnresolvedNameException unresolved)
        {
            return $"unknown: {unresolved.Name}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Preview for a frequency field authored as a coefficient + unit dropdown. The dropdown
    /// <paramref name="fieldUnit"/> is the SITE unit; the evaluator's var-unit-wins rule lets a
    /// unit-bearing reference (e.g. a GHz var) apply its OWN unit instead, so a mixed-unit compound
    /// (RFfreq + Voff) resolves exactly rather than via a single shared multiplier.
    /// </summary>
    public static string ComputeFreqPreview(string coeff, string fieldUnit, SchematicEditModel model)
    {
        string expr = coeff.Trim();
        if (expr.Length == 0) return "";

        // Bare-number + Hz: suppress (no "= 2.4" noise on a plain Hz literal).
        // Bare-number + other unit: show preview (confirms the multiplier applied).
        if (IsBareNumber(expr) && fieldUnit == "Hz") return "";

        try
        {
            var scope = DesignScope.BuildResolved(model);
            double hz = new Evaluator().Eval(expr, scope, unit: fieldUnit).AsReal();
            return Prefixed(FormatRealHonest(hz));
        }
        catch (UnresolvedNameException ex) { return $"unknown: {ex.Name}"; }
        catch { return ""; }
    }

    /// <summary>
    /// Resolves an expression to its RAW numeric value (units NOT applied), against the unit-stripped
    /// design scope. Used by the parametric-sweep row, which applies its own unit scaling on top of
    /// the coefficient. Returns false (value = 0) when the expression is not a resolvable real.
    /// </summary>
    public static bool TryResolveCoefficient(string expression, SchematicEditModel model, out double value)
    {
        value = 0;
        string trimmed = expression.Trim();
        if (trimmed.Length == 0) return false;
        try
        {
            var v = new Evaluator().Eval(trimmed, DesignScope.Build(model));   // Build = unit-stripped
            if (v.Kind != ValueKind.Real) return false;
            value = v.AsReal();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Formats an already-evaluated scalar <see cref="Value"/> with the HONEST "=" / "≈" prefix of
    /// expressions.md §9.1: "= value" when the displayed digits reconstruct the value (to ~1e-12
    /// relative — floating-point dust does not force "≈"), "≈ value" only when the value had to be
    /// rounded to fit the G4→G6→G8 display budget. Non-scalar kinds (Cube/Bool/String) yield "".
    /// Shared by the component-instance parameter editor so it matches the analysis-dialog hint.
    /// </summary>
    public static string FormatValueHonest(Value value) => value.Kind switch
    {
        ValueKind.Real    => Prefixed(FormatRealHonest(value.AsReal())),
        ValueKind.Complex => PrefixedComplex(value.AsComplex()),
        _                 => "",
    };

    // ── prefix + honest formatting ─────────────────────────────────────────────

    private static string Prefixed((string Text, bool Exact) f) => (f.Exact ? "= " : "≈ ") + f.Text;

    private static string PrefixedComplex(Complex c)
    {
        var (re, reEx) = FormatRealHonest(c.Real);
        var (im, imEx) = FormatRealHonest(Math.Abs(c.Imaginary));
        string sign = c.Imaginary < 0 ? "-" : "+";
        return (reEx && imEx ? "= " : "≈ ") + $"{re} {sign} {im}j";
    }

    /// <summary>
    /// Formats a real for display and reports whether the rendering is LOSSLESS. Widens precision
    /// (G4 → G6 → G8) until the text round-trips to the value (to ~1e-12 relative, so plain
    /// floating-point dust does NOT force "≈"); if none does within that display budget the value is
    /// shown at G4 and flagged approximate.
    /// </summary>
    private static (string Text, bool Exact) FormatRealHonest(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
            return (v.ToString(CultureInfo.InvariantCulture), true);

        foreach (var fmt in PrecisionLadder)
        {
            string s = v.ToString(fmt, CultureInfo.InvariantCulture);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
                && RoundTrips(back, v))
                return (s, true);
        }
        return (v.ToString("G4", CultureInfo.InvariantCulture), false);
    }

    private static readonly string[] PrecisionLadder = ["G4", "G6", "G8"];

    // True when a and b agree to ~1e-12 relative — i.e. the displayed digits reconstruct the value,
    // tolerating floating-point representation noise (2.4*1e9 vs the 2.4e9 literal).
    private static bool RoundTrips(double a, double b)
    {
        if (a == b) return true;
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return scale > 0 && Math.Abs(a - b) <= 1e-12 * scale;
    }

    private static bool IsBareNumber(string s)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                           CultureInfo.InvariantCulture, out _);
}
