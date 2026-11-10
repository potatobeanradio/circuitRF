using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The refusals of <c>value_at(x, v[, "axis"])</c> (Evaluator.Reductions.cs), as coded diagnostics rather than bare
/// sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class ValueAtDiagnostics
{
    public static Diagnostic PointNotNumber() => Diagnostic.Create(
        "measure.value-at.point", DiagnosticSeverity.Error,
        "value_at(): the point must be a number, e.g. value_at(x, 2.45GHz).");

    public static Diagnostic Scalar() => Diagnostic.Create(
        "measure.value-at.scalar", DiagnosticSeverity.Error,
        "value_at(): this value is a single number, so it has no axis to read along.");

    public static Diagnostic NoFreqAxis(string axes) => Diagnostic.Create(
        "measure.value-at.no-freq", DiagnosticSeverity.Error,
        "value_at(): this value has no 'freq' axis; name the axis as the last argument. Its axes are: {axes}.",
        ("axes", axes));

    public static Diagnostic NoSuchAxis(string axis, string axes) => Diagnostic.Create(
        "measure.value-at.no-axis", DiagnosticSeverity.Error,
        "value_at(): no axis named '{axis}'. Its axes are: {axes}.",
        ("axis", axis), ("axes", axes));

    public static Diagnostic EmptyAxis(string axis) => Diagnostic.Create(
        "measure.value-at.empty-axis", DiagnosticSeverity.Error,
        "value_at(): the '{axis}' axis has no points.",
        ("axis", axis));

    public static Diagnostic Outside(double point, string axis, string extent) => Diagnostic.Create(
        "measure.value-at.outside", DiagnosticSeverity.Error,
        "value_at(): {point} is outside the '{axis}' axis, which runs from {extent}.",
        ("point", point.ToString("G6", CultureInfo.InvariantCulture)), ("axis", axis), ("extent", extent));
}
