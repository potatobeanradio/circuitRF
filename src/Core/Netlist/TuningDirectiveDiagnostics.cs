using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// What the <c>.cnl</c> reader says about a <c>tune</c>, <c>preset</c>, <c>goal</c>, <c>optimize</c>,
/// <c>correlate</c>, <c>statistics</c> or <c>corner</c> line it cannot read (<see cref="TuningDirectiveText"/>).
/// </summary>
public static class TuningDirectiveDiagnostics
{
    public static Diagnostic UnknownKey(int line, string keyword, string key, string known) => Diagnostic.Create(
        "cnl.tuning.unknown-key", DiagnosticSeverity.Warning,
        "line {line}: '{keyword}' has no key '{key}'; it was kept but this version does not use it. Known keys: {known}.",
        ("line", line), ("keyword", keyword), ("key", key), ("known", known));

    public static Diagnostic Malformed(string keyword, string shape) => Diagnostic.Create(
        "cnl.tuning.malformed", DiagnosticSeverity.Error,
        "'{keyword}' is not written the way it reads: {shape}",
        ("keyword", keyword), ("shape", shape));

    public static Diagnostic KeyMalformed(string key) => Diagnostic.Create(
        "cnl.tuning.key-malformed", DiagnosticSeverity.Error,
        "'{key}' is not a tunable key. Spell it Instance.Parameter, Variable, or Cell:… of either.",
        ("key", key));

    public static Diagnostic NotKeyValue(string token) => Diagnostic.Create(
        "cnl.tuning.not-key-value", DiagnosticSeverity.Error,
        "'{token}' is not key=value.", ("token", token));

    public static Diagnostic ValueInvalid(string key, string value, string expected) => Diagnostic.Create(
        "cnl.tuning.value-invalid", DiagnosticSeverity.Error,
        "{key}={value} is not {expected}.", ("key", key), ("value", value), ("expected", expected));

    public static Diagnostic GoalProblem(string goal, string problem) => Diagnostic.Create(
        "cnl.tuning.goal-malformed", DiagnosticSeverity.Error,
        "goal {goal}: {problem}", ("goal", goal), ("problem", problem));

    public static Diagnostic SecondOptimize() => new(
        "cnl.tuning.optimize-repeated", DiagnosticSeverity.Error,
        "A netlist states at most one 'optimize' line.");

    // ── The statistical directives (docs/design/yield.md) ─────────────────────

    public static Diagnostic StatisticsMalformed(string keyword, string shape) => Diagnostic.Create(
        "cnl.statistics.malformed", DiagnosticSeverity.Error,
        "'{keyword}' is not written the way it reads: {shape}",
        ("keyword", keyword), ("shape", shape));

    public static Diagnostic TolPartNotAPartValue(string key) => Diagnostic.Create(
        "cnl.statistics.tol-part", DiagnosticSeverity.Error,
        "tune {key}: tol=part is a vendor part's own tolerance and goes on the part's value — Instance.C or Instance.L. Write tol= as a value or a percent here.",
        ("key", key));

    public static Diagnostic StatisticsValueInvalid(string key, string value, string expected) => Diagnostic.Create(
        "cnl.statistics.value-invalid", DiagnosticSeverity.Error,
        "{key}={value} is not {expected}.", ("key", key), ("value", value), ("expected", expected));

    public static Diagnostic SecondStatistics() => new(
        "cnl.statistics.repeated", DiagnosticSeverity.Error,
        "A netlist states at most one 'statistics' line.");

    public static Diagnostic SecondCenter() => new(
        "cnl.statistics.center-repeated", DiagnosticSeverity.Error,
        "A netlist states at most one 'center' line.");

    // ── The doe line (brief-yield-14 R-ya14-3) ─────────────────────────────────

    public static Diagnostic SecondDoe() => new(
        "cnl.doe.repeated", DiagnosticSeverity.Error,
        "A netlist states at most one 'doe' line.");

    public static Diagnostic DoeValueInvalid(string key, string value, string expected) => Diagnostic.Create(
        "cnl.doe.value-invalid", DiagnosticSeverity.Error,
        "doe {key}={value} is not {expected}.", ("key", key), ("value", value), ("expected", expected));

    public static Diagnostic DoeMalformed(string shape) => Diagnostic.Create(
        "cnl.doe.malformed", DiagnosticSeverity.Error,
        "'doe' is not written the way it reads: {shape}", ("shape", shape));

    // ── The sensitivity line (brief-sens-1 R-sens1-5) ──────────────────────────

    public static Diagnostic SecondSensitivity() => new(
        "cnl.sensitivity.duplicate", DiagnosticSeverity.Error,
        "A netlist states at most one 'sensitivity' line.");

    public static Diagnostic SensitivityValueInvalid(string key, string value, string expected) => Diagnostic.Create(
        "cnl.sensitivity.value-invalid", DiagnosticSeverity.Error,
        "sensitivity {key}={value} is not {expected}.", ("key", key), ("value", value), ("expected", expected));

    public static Diagnostic CornerMalformed(string corner, string problem) => Diagnostic.Create(
        "cnl.corner.malformed", DiagnosticSeverity.Error,
        "corner {corner}: {problem}", ("corner", corner), ("problem", problem));

    public static Diagnostic CornerValueInvalid(string corner, string key, string value, string expected) => Diagnostic.Create(
        "cnl.corner.value-invalid", DiagnosticSeverity.Error,
        "corner {corner}: {key}={value} is not {expected}.",
        ("corner", corner), ("key", key), ("value", value), ("expected", expected));
}

/// <summary>A tuning directive the reader refuses, carrying the diagnostic that says why. The
/// <c>.cnl</c> reader turns it into a <see cref="CnlReadException"/> naming the line.</summary>
public sealed class TuningDirectiveException(Diagnostic diagnostic) : FormatException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}
