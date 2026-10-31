using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// What <see cref="TuningValidator"/> says about a tuning setup — <c>check</c> reports these, and the
/// tuning and optimizer windows refuse on the errors among them. <c>{who}</c> names the line:
/// <c>tune R1.R</c>, <c>preset "wide band": R1.R</c>, <c>goal G1</c>, <c>optimize</c>.
/// </summary>
public static class TuningDiagnostics
{
    /// <summary>A key that names nothing — a warning, never an error (tuning-optimization.md D3).</summary>
    public static Diagnostic KeyUnresolved(string who, bool inPreset) => Diagnostic.Create(
        "tuning.key.unresolved", DiagnosticSeverity.Warning,
        inPreset ? "{who} names nothing in this design; recalling the preset skips it."
                 : "{who} names nothing in this design; it is ignored.",
        ("who", who));

    public static Diagnostic KeyNotOffered(string who, string why) => Diagnostic.Create(
        "tuning.key.not-offered", DiagnosticSeverity.Error,
        "{who} cannot be tuned: {why}.", ("who", who), ("why", why));

    public static Diagnostic NotANumber(string who, string what, string text) => Diagnostic.Create(
        "tuning.value.not-a-number", DiagnosticSeverity.Error,
        "{who}: the {what} '{text}' is not a number.", ("who", who), ("what", what), ("text", text));

    public static Diagnostic RangeInverted(string who, string min, string max) => Diagnostic.Create(
        "tuning.range.inverted", DiagnosticSeverity.Error,
        "{who}: min={min} is not below max={max}.", ("who", who), ("min", min), ("max", max));

    public static Diagnostic LogNeedsPositiveMin(string who, string min) => Diagnostic.Create(
        "tuning.range.log-nonpositive", DiagnosticSeverity.Error,
        "{who}: scale=log needs min above zero, and min={min}.", ("who", who), ("min", min));

    public static Diagnostic PhaseSpanTooWide(string who, string min, string max) => Diagnostic.Create(
        "tuning.range.phase-span", DiagnosticSeverity.Error,
        "{who}: min={min} to max={max} is more than one turn; a phase range spans at most 360 deg.",
        ("who", who), ("min", min), ("max", max));

    /// <summary>The ranges of one complex value's parts leave no value inside all of them (overview D18).</summary>
    public static Diagnostic ComplexRangesDisjoint(string whole, string entries) => Diagnostic.Create(
        "tuning.range.complex-disjoint", DiagnosticSeverity.Error,
        "{whole}: no complex value lies inside every range of its parts ({entries}).",
        ("whole", whole), ("entries", entries));

    public static Diagnostic PresetNameQuote(string name) => Diagnostic.Create(
        "tuning.preset.name-quote", DiagnosticSeverity.Error,
        "preset '{name}': a preset's name cannot contain a double quote.", ("name", name));

    public static Diagnostic ExpressionUnparsed(string who, string message) => Diagnostic.Create(
        "tuning.goal.expression", DiagnosticSeverity.Error,
        "{who}: the expression does not parse — {message}", ("who", who), ("message", message));

    public static Diagnostic AnalysisUndeclared(string who, string analysis, string declared) => Diagnostic.Create(
        "tuning.goal.analysis-undeclared", DiagnosticSeverity.Error,
        "{who}: analysis={analysis} is not an analysis this design declares ({declared}).",
        ("who", who), ("analysis", analysis), ("declared", declared));

    public static Diagnostic BandNeedsTwoLimits(string who, string type) => Diagnostic.Create(
        "tuning.goal.band-incomplete", DiagnosticSeverity.Error,
        "{who}: '{type}' needs two limits.", ("who", who), ("type", type));

    public static Diagnostic BandEmpty(string who, string limit) => Diagnostic.Create(
        "tuning.goal.band-empty", DiagnosticSeverity.Error,
        "{who}: both edges of the band are {limit}.", ("who", who), ("limit", limit));

    public static Diagnostic SlopeNeedsRange(string who) => Diagnostic.Create(
        "tuning.goal.slope-without-range", DiagnosticSeverity.Error,
        "{who}: a sloped limit needs a range (over=… lo=… hi=…) to slope across.", ("who", who));

    public static Diagnostic GoalRangeEmpty(string who, string lo, string hi, string axis, string analysis,
                                            double gridMin, double gridMax) => Diagnostic.Create(
        "tuning.goal.range-empty", DiagnosticSeverity.Error,
        "{who}: the range {lo} .. {hi} on {axis} holds no point of {analysis}'s grid, which runs {gridMin} .. {gridMax} in base SI.",
        ("who", who), ("lo", lo), ("hi", hi), ("axis", axis), ("analysis", analysis),
        ("gridMin", gridMin), ("gridMax", gridMax));

    public static Diagnostic GoalScaleNotPositive(string who, string scale) => Diagnostic.Create(
        "tuning.goal.scale", DiagnosticSeverity.Error,
        "{who}: scale={scale} must be above zero.", ("who", who), ("scale", scale));

    public static Diagnostic UnknownAlgorithm(string algorithm, string known) => Diagnostic.Create(
        "tuning.optimize.algorithm", DiagnosticSeverity.Error,
        "optimize: algorithm={algorithm} is not one of {known}.", ("algorithm", algorithm), ("known", known));

    public static Diagnostic UnknownAlgorithmOption(string option, string algorithm, string known) => Diagnostic.Create(
        "tuning.optimize.option", DiagnosticSeverity.Warning,
        "optimize: alg.{option} is not an option of {algorithm} and is ignored; it takes {known}.",
        ("option", option), ("algorithm", algorithm), ("known", known));

    public static Diagnostic AlgorithmOptionChoice(string option, string value, string choices) => Diagnostic.Create(
        "tuning.optimize.option-value", DiagnosticSeverity.Error,
        "optimize: alg.{option}={value} is not {choices}.", ("option", option), ("value", value), ("choices", choices));

    public static Diagnostic TimeLimitInvalid(string limit) => Diagnostic.Create(
        "tuning.optimize.timelimit", DiagnosticSeverity.Error,
        "optimize: timelimit={limit} is not a duration; write a number and s, ms, min or h.", ("limit", limit));

    // ── Vendor parts (brief vendorlib-4) ────────────────────────────────────

    public static Diagnostic PartNoCandidates(string who, string selection, string ports) => Diagnostic.Create(
        "tuning.part.no-candidates", DiagnosticSeverity.Error,
        "{who}: {selection} finds no part to choose among{ports}.",
        ("who", who), ("selection", selection), ("ports", ports));

    public static Diagnostic PartUnreadable(string who, string parts) => Diagnostic.Create(
        "tuning.part.unreadable", DiagnosticSeverity.Warning,
        "{who}: these candidates could not be read and are left out: {parts}.",
        ("who", who), ("parts", parts));

    public static Diagnostic PartNotStatistical(string who) => Diagnostic.Create(
        "tuning.part.not-statistical", DiagnosticSeverity.Error,
        "{who}: a vendor part is chosen, never drawn — its static model states no tolerance. Remove dist= and its spread.",
        ("who", who));

    public static Diagnostic PartLevelsAreCandidates(string who) => Diagnostic.Create(
        "tuning.part.levels", DiagnosticSeverity.Error,
        "{who}: a vendor part's levels are its candidate parts; discrete=, step= and scale= do not apply. Narrow the candidates with min=/max= instead.",
        ("who", who));

    public static Diagnostic PartSelectionNotAPart(string who) => Diagnostic.Create(
        "tuning.part.not-a-part", DiagnosticSeverity.Error,
        "{who}: kind= and parts= choose a placed vendor part's candidates, and this key is not one (Instance.Part of a vendor library part).",
        ("who", who));
}
